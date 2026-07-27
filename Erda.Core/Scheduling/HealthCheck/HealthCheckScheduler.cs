using Erda.Core.Configuration;
using Erda.Core.Services;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Options;

namespace Erda.Core.Scheduling;

/// <summary>
/// Background loop: every <c>HealthCheck:Interval</c>, send a tiny probe prompt through the
/// <see cref="IReasoner"/> seam (the same streamed OpenAI Responses path the chat agent uses) to
/// confirm the local OpenAI-compatible endpoint (codex/proxy) is still answering. A probe fails on
/// an exception, a timeout, or empty output. When the connection goes down Phil gets one WhatsApp
/// alert (and, if <c>ReAlertAfter</c> is set, a repeat after that cooldown while it stays down); when
/// it comes back he gets a recovery note. State is in-memory only — a restart just re-establishes the
/// baseline on the first probe. Mirrors the loop shape of <see cref="ErrorWatchScheduler"/>.
/// </summary>
public sealed class HealthCheckScheduler(
    IOptions<HealthCheckOptions> healthCheckOptions,
    IOptions<WhatsAppOptions> whatsAppOptions,
    IReasoner reasoner,
    IWhatsAppSender sender,
    IActivityRecorder recorder,
    IClock clock,
    ILogger<HealthCheckScheduler> logger) : BackgroundService
{
    // A deterministic, cheap probe: minimal reasoning effort, no web search, tiny reply.
    private const string ProbePrompt = "Connection health check. Reply with the single word: ok";

    // Outage tracking (in-memory). _lastHealthy is null until the first probe completes.
    private bool? _lastHealthy;
    private DateTimeOffset? _downSince;
    private DateTimeOffset? _lastAlertUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = healthCheckOptions.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Health-check scheduler disabled (HealthCheck:Enabled=false).");
            return;
        }

        var ownerJid = WhatsAppJid.FromNumber(whatsAppOptions.Value.OwnerNumber);
        if (string.IsNullOrEmpty(ownerJid))
            logger.LogWarning("Health-check scheduler: WhatsApp owner number not configured; alerts can't be delivered.");

        logger.LogInformation(
            "Health-check scheduler started: every {Interval}, timeout {Timeout}.", opts.Interval, opts.EffectiveTimeout);

        using var timer = new PeriodicTimer(opts.Interval);
        do
        {
            try
            {
                await CheckOnceAsync(opts, ownerJid, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Health-check cycle failed unexpectedly; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>One probe cycle: check the endpoint, then alert/recover as the health transitions. Exposed for testing.</summary>
    public async Task CheckOnceAsync(HealthCheckOptions opts, string ownerJid, CancellationToken ct)
    {
        var (ok, detail) = await ProbeAsync(opts.EffectiveTimeout, ct);
        var now = clock.UtcNow;

        if (ok)
        {
            if (_lastHealthy == false)
            {
                var downFor = _downSince is { } since ? now - since : (TimeSpan?)null;
                logger.LogInformation("Health-check: OpenAI/chat connection recovered after {Down}.", downFor);
                recorder.Record("health_check", "OpenAI/chat connection recovered", new { downFor });
                await AlertAsync(ownerJid, FormatRecovery(downFor), ct);
            }
            _lastHealthy = true;
            _downSince = null;
            _lastAlertUtc = null;
            return;
        }

        // Unhealthy.
        var firstFailure = _lastHealthy != false;
        if (firstFailure)
            _downSince = now;

        var cooldownDue = opts.ReAlertAfter is { } window
            && _lastAlertUtc is { } last
            && now - last >= window;

        logger.LogWarning("Health-check: OpenAI/chat probe failed: {Detail}", detail);

        if (firstFailure || cooldownDue)
        {
            recorder.Record("health_check", "OpenAI/chat connection down", new { detail });
            await AlertAsync(ownerJid, FormatFailure(detail, firstFailure ? null : now - _downSince), ct);
            _lastAlertUtc = now;
        }

        _lastHealthy = false;
    }

    /// <summary>Runs the probe under a timeout. Returns whether the endpoint answered, plus a short reason on failure.</summary>
    private async Task<(bool Ok, string Detail)> ProbeAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var reply = await reasoner.ReasonAsync(
                ProbePrompt, webSearch: false, cts.Token, logLabel: "health-check", reasoningEffort: "low");
            return string.IsNullOrWhiteSpace(reply)
                ? (false, "endpoint returned an empty response")
                : (true, "ok");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutting down — let the loop break, don't treat as a failure
        }
        catch (OperationCanceledException)
        {
            return (false, $"timed out after {timeout}");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task AlertAsync(string ownerJid, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ownerJid))
        {
            logger.LogInformation("Health-check alert (not delivered — no owner configured): {Text}", text);
            return;
        }
        if (!await sender.SendAsync(ownerJid, text, ct))
            logger.LogWarning("Failed to deliver health-check alert to owner.");
    }

    internal static string FormatFailure(string detail, TimeSpan? downFor) =>
        downFor is { } d
            ? $"⚠️ OpenAI/chat connection still down after {Humanize(d)} — {detail}. Check the codex proxy/endpoint."
            : $"⚠️ OpenAI/chat connection check failed — {detail}. Check the codex proxy/endpoint.";

    internal static string FormatRecovery(TimeSpan? downFor) =>
        downFor is { } d
            ? $"✅ OpenAI/chat connection recovered (was down for {Humanize(d)})."
            : "✅ OpenAI/chat connection recovered.";

    private static string Humanize(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalMinutes < 1)
            return $"{(int)age.TotalSeconds}s";
        if (age.TotalHours < 1)
            return $"{(int)age.TotalMinutes} min";
        if (age.TotalHours < 48)
            return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
