using System.Diagnostics;
using System.Text;
using Erda.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Services;

/// <summary>
/// Shells out to the <c>codex</c> CLI in non-interactive <c>exec</c> mode.
/// Codex authenticates with Phil's ChatGPT subscription (auth lives in ~/.codex).
///
/// HARD RULE: OPENAI_API_KEY is removed from the child process environment so Codex
/// never falls back to pay-per-token API-key billing.
/// </summary>
public sealed class CodexRunner(IOptions<ErdaOptions> options, ILogger<CodexRunner> logger)
{
    /// <summary>
    /// Voice-memo convenience: builds the prompt from a developer instruction + transcript,
    /// then runs Codex. Kept for the voice-memo workflow's CodexExecutor.
    /// </summary>
    public Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken cancellationToken = default)
        => RunPromptAsync($"{developerInstruction}\n\nTranscript:\n{transcript}", enableWebSearch: true, cancellationToken, logLabel: "voice-memo processing");

    /// <summary>
    /// Runs <c>codex exec</c> on an already-built prompt and returns Codex's final message.
    /// General-purpose entry point. <paramref name="enableWebSearch"/> turns on Codex's native
    /// web_search tool (for grounding answers in current sources); when searching we use a
    /// read-only sandbox since no files need writing.
    /// </summary>
    public async Task<string> RunPromptAsync(
        string prompt, bool enableWebSearch = false, CancellationToken cancellationToken = default, string? logLabel = null)
    {
        var opts = options.Value;
        var workDir = Directory.CreateTempSubdirectory("erda-codex-").FullName;
        var outputFile = Path.Combine(workDir, "codex-final.txt");
        var sandbox = enableWebSearch ? "read-only" : "workspace-write";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "codex",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workDir,
            };

            // Note: arguments go through ArgumentList (no shell), so config values keep their
            // quotes for codex's TOML override parser, e.g. model_reasoning_effort="high".
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(opts.CodexModel);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"model_reasoning_effort=\"{opts.CodexReasoningEffort}\"");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("preferred_auth_method=\"chatgpt\"");
            if (enableWebSearch)
            {
                // Enable Codex's native Responses web_search tool.
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("tools.web_search=true");
            }
            psi.ArgumentList.Add("--cd");
            psi.ArgumentList.Add(workDir);
            psi.ArgumentList.Add("--sandbox");
            psi.ArgumentList.Add(sandbox);
            psi.ArgumentList.Add("--skip-git-repo-check");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputFile);
            psi.ArgumentList.Add(prompt);

            // CRITICAL: ensure the platform key is NOT in the child env, so Codex authenticates
            // with the ChatGPT subscription rather than per-token API billing. (If OPENAI_API_KEY
            // lives only in appsettings it is in IConfiguration but never in the OS process env,
            // so it was never inherited and Remove is a no-op.)
            psi.Environment.Remove("OPENAI_API_KEY");
            var keyAbsentFromChild = !psi.Environment.ContainsKey("OPENAI_API_KEY");

            // Log WHAT was asked (logLabel = the question/task), not a preview of the raw prompt —
            // whose first chars are always the fixed developer instruction. Content is shown only
            // when message-content capture is on (same env var MAF's OpenTelemetry uses).
            var captureContent = string.Equals(
                Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"),
                "true", StringComparison.OrdinalIgnoreCase);
            var rawTask = string.IsNullOrWhiteSpace(logLabel) ? prompt : logLabel!;
            var task = captureContent
                ? (rawTask.Length > 160 ? rawTask[..160].Replace('\n', ' ') + "…" : rawTask.Replace('\n', ' '))
                : "(hidden; set OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true)";

            logger.LogInformation(
                "Codex exec: model={Model} effort={Effort} webSearch={Web} sandbox={Sandbox} promptChars={Chars} | task={Task} | OPENAI_API_KEY absent from child: {Absent}",
                opts.CodexModel, opts.CodexReasoningEffort, enableWebSearch, sandbox, prompt.Length, task, keyAbsentFromChild);

            var sw = Stopwatch.StartNew();

            using var proc = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            try
            {
                proc.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to launch the 'codex' CLI. Ensure codex-cli is installed, logged in, and on PATH.", ex);
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(cancellationToken);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"codex exec failed (exit {proc.ExitCode}): {stderr.ToString().Trim()}");

            var fromFile = File.Exists(outputFile)
                ? (await File.ReadAllTextAsync(outputFile, cancellationToken)).Trim()
                : string.Empty;
            string result;
            if (fromFile.Length > 0)
            {
                result = fromFile;
            }
            else
            {
                result = stdout.ToString().Trim();
                if (result.Length == 0)
                    throw new InvalidOperationException("Codex produced no output.");
            }

            logger.LogInformation("Codex completed in {ElapsedMs}ms, returned {Chars} chars.", sw.ElapsedMilliseconds, result.Length);
            return result;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
