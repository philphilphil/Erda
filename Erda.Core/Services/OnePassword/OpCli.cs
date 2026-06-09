using System.Diagnostics;
using System.Text;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services.OnePassword;

/// <summary>
/// Real <see cref="IOpCli"/>: shells out to the <c>op</c> CLI. Mirrors <see cref="Erda.Core.Services.PreScriptRunner"/>'s
/// subprocess handling (no shell, closed stdin → EOF, bounded timeout, process-tree kill). The
/// environment is inherited unchanged so <c>op</c> picks up <c>OP_SERVICE_ACCOUNT_TOKEN</c>.
///
/// Logging is deliberately minimal and value-free: it logs the argv (which only ever contains
/// references / item ids / flags — never secret values) and timing, never stdout.
/// </summary>
public sealed class OpCli(IOptions<BrowserOptions> options, ILogger<OpCli> logger) : IOpCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.Value.OpCommand,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var cmd = args.Count > 0 ? args[0] : "(no command)";

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new OpCliException(
                $"Failed to launch the '{options.Value.OpCommand}' CLI. Ensure the 1Password CLI is installed and on PATH. ({ex.Message})");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close(); // EOF — op never reads stdin here

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new OpCliException($"op {cmd} exceeded {Timeout} and was killed.");
        }

        if (proc.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            var tail = err.Length > 300 ? "…" + err[^300..] : err;
            // argv is safe to log (references/ids/flags only). stderr from op does not echo values.
            logger.LogWarning("op {Args} failed (exit {Exit}) in {Ms}ms: {Err}",
                string.Join(' ', args), proc.ExitCode, sw.ElapsedMilliseconds, tail);
            throw new OpCliException($"op {cmd} failed (exit {proc.ExitCode}): {tail}");
        }

        logger.LogDebug("op {Args} ok in {Ms}ms", string.Join(' ', args), sw.ElapsedMilliseconds);
        return stdout.ToString().Trim();
    }
}
