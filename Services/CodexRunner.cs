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
    public async Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var workDir = Directory.CreateTempSubdirectory("erda-codex-").FullName;
        var outputFile = Path.Combine(workDir, "codex-final.txt");
        var prompt = $"{developerInstruction}\n\nTranscript:\n{transcript}";

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
            psi.ArgumentList.Add("--cd");
            psi.ArgumentList.Add(workDir);
            psi.ArgumentList.Add("--sandbox");
            psi.ArgumentList.Add("workspace-write");
            psi.ArgumentList.Add("--skip-git-repo-check");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputFile);
            psi.ArgumentList.Add(prompt);

            // CRITICAL: ensure the platform key is NOT in the child env, so Codex authenticates
            // with the ChatGPT subscription rather than per-token API billing. Remove() returns
            // true if it was present (and is now stripped), false if it was never there. Either
            // way the child cannot see it. (If OPENAI_API_KEY lives only in appsettings, it is
            // in IConfiguration but never in the OS process env, so it was never inherited.)
            var wasPresentAndStripped = psi.Environment.Remove("OPENAI_API_KEY");
            var keyAbsentFromChild = !psi.Environment.ContainsKey("OPENAI_API_KEY");

            var preview = prompt.Length > 120 ? prompt[..120].Replace('\n', ' ') + "…" : prompt.Replace('\n', ' ');
            logger.LogInformation(
                "Launching Codex: codex exec -m {Model} -c model_reasoning_effort=\"{Effort}\" " +
                "-c preferred_auth_method=\"chatgpt\" --cd {Dir} --sandbox workspace-write --skip-git-repo-check " +
                "-o {Out} \"{Preview}\"  |  OPENAI_API_KEY absent from child env: {Absent} (was present & stripped: {Stripped})",
                opts.CodexModel, opts.CodexReasoningEffort, workDir, outputFile, preview, keyAbsentFromChild, wasPresentAndStripped);

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

            if (File.Exists(outputFile))
            {
                var fromFile = (await File.ReadAllTextAsync(outputFile, cancellationToken)).Trim();
                if (fromFile.Length > 0)
                    return fromFile;
            }

            var fromStdout = stdout.ToString().Trim();
            if (fromStdout.Length == 0)
                throw new InvalidOperationException("Codex produced no output.");
            return fromStdout;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
