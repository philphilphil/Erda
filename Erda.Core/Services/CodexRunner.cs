using System.Diagnostics;
using System.Text;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services;

/// <summary>
/// Shells out to the <c>codex</c> CLI in non-interactive <c>exec</c> mode.
/// Codex authenticates with Phil's ChatGPT subscription (auth lives in ~/.codex).
///
/// HARD RULE: OPENAI_API_KEY is removed from the child process environment so Codex
/// never falls back to pay-per-token API-key billing.
/// </summary>
public sealed class CodexRunner(IOptions<ErdaOptions> options, ILogger<CodexRunner> logger) : ICodexRunner
{
    /// <summary>
    /// Voice-memo convenience: builds the prompt from a developer instruction + transcript,
    /// then runs Codex. Kept for the voice-memo workflow's CodexExecutor.
    /// </summary>
    public Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken cancellationToken = default)
        => RunPromptAsync($"{developerInstruction}\n\nTranscript:\n{transcript}", enableWebSearch: true, cancellationToken, logLabel: "voice-memo processing");

    /// <summary>The reasoning-effort levels Codex (gpt-5.x) accepts.</summary>
    private static readonly HashSet<string> ValidEfforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "minimal", "low", "medium", "high",
    };

    /// <summary>Normalize a requested reasoning effort to a known level, or fall back to the default.</summary>
    public static string NormalizeReasoningEffort(string? requested, string fallback)
    {
        var trimmed = requested?.Trim();
        return !string.IsNullOrEmpty(trimmed) && ValidEfforts.Contains(trimmed)
            ? trimmed.ToLowerInvariant()
            : fallback;
    }

    /// <summary>
    /// Stderr substrings that mean Codex's ChatGPT session is expired/invalidated (re-login needed),
    /// as opposed to any other failure (bad model, sandbox, etc.). Kept specific to avoid false
    /// positives on unrelated errors.
    /// </summary>
    private static readonly string[] AuthFailureMarkers =
    {
        "token_invalidated",
        "refresh_token_reused",
        "invalid_grant",
        "sign in again",
        "log out and sign in",
        "not logged in",
        "could not be refreshed",
    };

    /// <summary>True when <paramref name="stderr"/> indicates a Codex authentication failure.</summary>
    private static bool IsAuthFailure(string stderr) =>
        AuthFailureMarkers.Any(m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs <c>codex exec</c> on an already-built prompt and returns Codex's final message.
    /// General-purpose entry point. <paramref name="enableWebSearch"/> turns on Codex's native
    /// web_search tool (for grounding answers in current sources). We always run with the
    /// <c>workspace-write</c> sandbox plus <c>network_access=true</c> so model-run shell commands can
    /// reach the network (curl/fetch); file writes stay confined to the throwaway temp work dir.
    /// </summary>
    public async Task<string> RunPromptAsync(
        string prompt, bool enableWebSearch = false, CancellationToken cancellationToken = default,
        string? logLabel = null, string? reasoningEffort = null)
    {
        var opts = options.Value;
        var effort = NormalizeReasoningEffort(reasoningEffort, opts.CodexReasoningEffort);
        var workDir = Directory.CreateTempSubdirectory("erda-codex-").FullName;
        var outputFile = Path.Combine(workDir, "codex-final.txt");
        // workspace-write (not read-only) is required for network_access to take effect; read-only
        // blocks all egress, which broke prompts that need to fetch URLs.
        const string sandbox = "workspace-write";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = opts.CodexExecutable,
                RedirectStandardInput = true,  // so we can close it → EOF (codex exec reads stdin)
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
            psi.ArgumentList.Add($"model_reasoning_effort=\"{effort}\"");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("preferred_auth_method=\"chatgpt\"");
            // Allow model-run shell commands full network egress (verified: workspace-write blocks
            // the network by default; this override re-enables it). Erda runs on a trusted LAN host.
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("sandbox_workspace_write.network_access=true");
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
                opts.CodexModel, effort, enableWebSearch, sandbox, prompt.Length, task, keyAbsentFromChild);

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

            // codex exec reads stdin for piped input ("Reading additional input from stdin..."). We
            // pipe nothing, so close it immediately to send EOF. Without this, codex inherits Erda's
            // stdin (e.g. a never-closing socket under `make dev-wa`) and blocks in read() forever.
            proc.StandardInput.Close();

            // Bound the run so a stuck codex can't wedge the caller (e.g. the reminder poll loop).
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(opts.CodexTimeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new TimeoutException($"codex exec exceeded {opts.CodexTimeout} and was killed.");
            }

            if (proc.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                if (IsAuthFailure(err))
                {
                    // Keep the raw stderr in the logs for debugging, but hand the agent (and thus
                    // the user) a short, actionable message instead of a wall of 401 traces.
                    logger.LogWarning(
                        "Codex auth failure (exit {Exit}) — run `codex login` to re-authenticate. stderr: {Stderr}",
                        proc.ExitCode, err);
                    throw new InvalidOperationException(
                        "Codex isn't logged in — its ChatGPT session has expired or been invalidated. " +
                        "Run `codex login` on the host to re-authenticate, then try again.");
                }

                throw new InvalidOperationException($"codex exec failed (exit {proc.ExitCode}): {err}");
            }

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
