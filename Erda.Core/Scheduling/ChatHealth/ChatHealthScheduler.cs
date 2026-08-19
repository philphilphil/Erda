using Erda.Core.Configuration;
using Erda.Core.Services;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Options;

namespace Erda.Core.Scheduling;

/// <summary>
/// What the watch remembers between checks: the outage it is currently in, and when it last said so.
/// Deliberately in-memory — an outage that survives a restart is re-announced on the next check,
/// which is what you want from a process that just came back up.
/// </summary>
public sealed class ChatHealthState
{
    /// <summary>When the current outage started; null while the endpoint is healthy.</summary>
    public DateTimeOffset? DownSince { get; set; }

    /// <summary>When the current outage was last announced; null if it never was (no owner configured).</summary>
    public DateTimeOffset? LastAlerted { get; set; }
}

/// <summary>
/// Background loop: every <c>ChatHealth:CheckInterval</c>, send a trivial prompt through the chat
/// endpoint (the local OpenAI-compatible proxy) and WhatsApp Phil when it stops answering — the proxy
/// shut down, logged itself out, or hands back empty responses. Without this the first sign of an
/// outage is a voice memo or agent turn silently failing hours later.
///
/// One alert per outage (plus an optional <c>ReAlertAfter</c> nag while it stays down) and one notice
/// when it recovers. A single failed probe is retried after <see cref="RetryDelay"/> before an outage
/// is declared, so one blip doesn't page anyone.
/// </summary>
public sealed class ChatHealthScheduler(
    IOptions<ChatHealthOptions> healthOptions,
    IOptions<ErdaOptions> erdaOptions,
    IOptions<WhatsAppOptions> whatsAppOptions,
    IChatHealthProbe probe,
    IWhatsAppSender sender,
    IActivityRecorder recorder,
    IClock clock,
    ILogger<ChatHealthScheduler> logger) : BackgroundService
{
    /// <summary>Probes per check: a second one confirms a failure before it counts as an outage.</summary>
    public const int ProbeAttempts = 2;

    /// <summary>Pause between the two probes of a failing check; shortened by tests.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Grace period before the first check, so a cold boot (Erda up before the proxy) doesn't alert;
    /// shortened by tests.
    /// </summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = healthOptions.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Chat-health watch disabled (ChatHealth:Enabled=false).");
            return;
        }

        var ownerJid = WhatsAppJid.FromNumber(whatsAppOptions.Value.OwnerNumber);
        if (string.IsNullOrEmpty(ownerJid))
            logger.LogWarning("Chat-health watch: WhatsApp owner number not configured; outages will only be logged.");

        logger.LogInformation(
            "Chat-health watch started: every {Interval}, probe timeout {Timeout}, endpoint {Endpoint}.",
            opts.CheckInterval, opts.Timeout, erdaOptions.Value.ChatBaseUrl);

        var state = new ChatHealthState();
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(opts.CheckInterval);
        do
        {
            try
            {
                await CheckOnceAsync(opts, state, ownerJid, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat-health check failed unexpectedly; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>One check cycle: probe, then alert / re-alert / announce recovery. Exposed for testing.</summary>
    public async Task CheckOnceAsync(
        ChatHealthOptions opts, ChatHealthState state, string ownerJid, CancellationToken ct)
    {
        var result = await ProbeWithRetryAsync(opts, ct);
        var now = clock.UtcNow;

        if (result.Ok)
        {
            await OnHealthyAsync(state, ownerJid, now, result, ct);
            return;
        }

        await OnFailedAsync(opts, state, ownerJid, now, result, ct);
    }

    /// <summary>Probes up to <see cref="ProbeAttempts"/> times; the first success wins.</summary>
    private async Task<ChatProbeResult> ProbeWithRetryAsync(ChatHealthOptions opts, CancellationToken ct)
    {
        ChatProbeResult result = new(false, "not probed", TimeSpan.Zero);
        for (var attempt = 1; attempt <= ProbeAttempts; attempt++)
        {
            result = await probe.ProbeAsync(opts.Timeout, ct);
            if (result.Ok)
                return result;

            logger.LogWarning(
                "Chat-health probe {Attempt}/{Attempts} failed: {Error}", attempt, ProbeAttempts, result.Error);
            if (attempt < ProbeAttempts)
                await Task.Delay(RetryDelay, ct);
        }
        return result;
    }

    /// <summary>The endpoint answered: close an open outage (announcing it if we announced its start).</summary>
    private async Task OnHealthyAsync(
        ChatHealthState state, string ownerJid, DateTimeOffset now, ChatProbeResult result, CancellationToken ct)
    {
        if (state.DownSince is not { } downSince)
        {
            logger.LogDebug("Chat-health check ok in {ElapsedMs}ms.", (int)result.Elapsed.TotalMilliseconds);
            return;
        }

        var downFor = now - downSince;
        var announced = state.LastAlerted is not null;
        state.DownSince = null;
        state.LastAlerted = null;

        logger.LogInformation("Chat endpoint recovered after {DownFor}.", downFor);
        recorder.Record(
            "chat_health",
            $"OpenAI proxy recovered after {ChatHealthAlert.Humanize(downFor)}",
            new { status = "recovered", downForSeconds = (int)downFor.TotalSeconds });

        // Only close the loop for an outage Phil was actually told about.
        if (announced && !string.IsNullOrEmpty(ownerJid))
            await sender.SendAsync(ownerJid, ChatHealthAlert.FormatRecovered(erdaOptions.Value.ChatBaseUrl, downFor), ct);
    }

    /// <summary>The endpoint failed: open an outage and alert, or nag if <c>ReAlertAfter</c> elapsed.</summary>
    private async Task OnFailedAsync(
        ChatHealthOptions opts, ChatHealthState state, string ownerJid, DateTimeOffset now,
        ChatProbeResult result, CancellationToken ct)
    {
        var isNew = state.DownSince is null;
        state.DownSince ??= now;

        if (!ShouldAlert(state, opts.ReAlertAfter, now))
        {
            logger.LogWarning("Chat endpoint still down since {DownSince:u}; alert suppressed.", state.DownSince);
            return;
        }

        var downFor = isNew ? (TimeSpan?)null : now - state.DownSince.Value;
        var text = ChatHealthAlert.FormatDown(
            erdaOptions.Value.ChatBaseUrl, erdaOptions.Value.ChatModel, result.Error, downFor);

        // Warning, not Error, on purpose: error-watch polls Seq at Error level and would send Phil a
        // second WhatsApp message for the same outage — with an analysis the down endpoint can't produce.
        logger.LogWarning("Chat endpoint is not answering ({Error}).", result.Error);
        recorder.Record(
            "chat_health",
            isNew ? "OpenAI proxy is not answering" : "OpenAI proxy is still not answering",
            new { status = "down", error = result.Error });

        if (string.IsNullOrEmpty(ownerJid))
        {
            state.LastAlerted = null; // nothing was delivered; a later check may still find an owner
            return;
        }

        // Record the attempt either way: a bridge that is also down must not turn ReAlertAfter into a
        // per-check alert storm once it comes back.
        state.LastAlerted = now;
        if (!await sender.SendAsync(ownerJid, text, ct))
            logger.LogWarning("Failed to deliver the chat-health alert over WhatsApp.");
    }

    /// <summary>Alert on a fresh outage, or on an ongoing one once the re-alert cooldown elapsed.</summary>
    private static bool ShouldAlert(ChatHealthState state, TimeSpan? reAlertAfter, DateTimeOffset now)
    {
        if (state.LastAlerted is not { } last)
            return true;
        return reAlertAfter is { } window && now - last >= window;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
