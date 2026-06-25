using System.Diagnostics;
using System.Text;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services;

/// <summary>
/// Runs a scheduled prompt's optional pre-run shell command and returns its stdout, to be injected
/// into the prompt as context. Careful subprocess handling: a fresh temp working directory, closed
/// stdin, a bounded timeout, and a process-tree kill.
///
/// The command is taken verbatim from a reminder row that only Phil can edit via the panel — never
/// the agent — so this is deliberate, bounded code execution inside the panel's trust boundary.
/// OPENAI_API_KEY is intentionally NOT stripped from the environment: a context script may legitimately
/// need already-configured secrets.
/// </summary>
public sealed class PreScriptRunner(IOptions<ReminderOptions> options, ILogger<PreScriptRunner> logger)
    : IPreScriptRunner
{
    public async Task<string> RunAsync(string script, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var workDir = Directory.CreateTempSubdirectory("erda-prescript-").FullName;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardInput = true,  // so we can close it → EOF
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workDir,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);

            // Log the script text only when message-content capture is on.
            var captureContent = string.Equals(
                Environment.GetEnvironmentVariable("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"),
                "true", StringComparison.OrdinalIgnoreCase);

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
                    "Failed to launch the pre-run script via /bin/sh. Check the command and that /bin/sh exists.", ex);
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Close stdin immediately to send EOF; otherwise the child can inherit a never-closing
            // stdin and block in read() forever.
            proc.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(opts.PreScriptTimeout);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new TimeoutException($"Pre-run script exceeded {opts.PreScriptTimeout} and was killed.");
            }

            if (proc.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                var tail = err.Length > 300 ? "…" + err[^300..] : err;
                throw new InvalidOperationException($"Pre-run script failed (exit {proc.ExitCode}): {tail}");
            }

            var result = stdout.ToString().Trim();
            if (result.Length > opts.PreScriptMaxOutputChars)
                result = result[..opts.PreScriptMaxOutputChars] + "\n…[context truncated]";

            logger.LogInformation(
                "Pre-run script: scriptChars={ScriptChars} elapsedMs={ElapsedMs} outputChars={OutputChars} exit={Exit} | script={Script}",
                script.Length, sw.ElapsedMilliseconds, result.Length, proc.ExitCode,
                captureContent
                    ? (script.Length > 160 ? script[..160].Replace('\n', ' ') + "…" : script.Replace('\n', ' '))
                    : "(hidden; set OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true)");
            return result;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
