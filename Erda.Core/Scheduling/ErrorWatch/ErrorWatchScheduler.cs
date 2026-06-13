using Erda.Core.Configuration;
using Erda.Core.Services;
using Erda.Core.Services.Seq;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Options;

namespace Erda.Core.Scheduling;

/// <summary>
/// Background loop: every <c>ErrorWatch:PollInterval</c>, query Seq for new errors (≥ MinLevel),
/// dedup by signature, analyze each new one with Codex, and push the analysis to Phil on WhatsApp.
/// Watermark + dedup memory are persisted so restarts don't replay or double-alert.
/// </summary>
public sealed class ErrorWatchScheduler(
    IOptions<ErrorWatchOptions> errorWatchOptions,
    IOptions<SeqOptions> seqOptions,
    IOptions<WhatsAppOptions> whatsAppOptions,
    ISeqClient seq,
    IErrorAnalyzer analyzer,
    IWhatsAppSender sender,
    ErrorWatchStateStore store,
    IActivityRecorder recorder,
    IClock clock,
    ILogger<ErrorWatchScheduler> logger) : BackgroundService
{
    private const int QueryCount = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = errorWatchOptions.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Error-watch scheduler disabled (ErrorWatch:Enabled=false).");
            return;
        }
        if (!seqOptions.Value.HasServer)
        {
            logger.LogWarning("Error-watch scheduler enabled but Seq:ServerUrl is not set; not starting.");
            return;
        }

        var ownerJid = WhatsAppJid.FromNumber(whatsAppOptions.Value.OwnerNumber);
        if (string.IsNullOrEmpty(ownerJid))
            logger.LogWarning("Error-watch scheduler: WhatsApp owner number not configured; alerts can't be delivered.");

        var state = store.Load();
        if (state.LastTimestampUtc is null)
        {
            state.LastTimestampUtc = clock.UtcNow; // first run: start from now, don't replay history
            store.Save(state);
            logger.LogInformation("Error-watch scheduler: first run, watermark set to {Now:u}.", state.LastTimestampUtc);
        }

        logger.LogInformation("Error-watch scheduler started: every {Interval}, level >= {Level}.", opts.PollInterval, opts.MinLevel);

        using var timer = new PeriodicTimer(opts.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(opts, store, state, ownerJid, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error-watch poll failed; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>One poll cycle. Exposed for integration testing.</summary>
    public async Task PollOnceAsync(
        ErrorWatchOptions opts, ErrorWatchStateStore store, ErrorWatchState state, string ownerJid, CancellationToken ct)
    {
        var filter = SeqFilter.ForMinLevel(opts.MinLevel, opts.Filter);
        var events = await seq.QueryErrorsAsync(filter, state.LastTimestampUtc, QueryCount, ct);
        if (events.Count == 0)
            return;

        // Oldest first; drop events already processed in a previous poll (boundary duplicates).
        var ordered = events
            .Where(e => !string.IsNullOrEmpty(e.Id) && !state.SeenEventIds.Contains(e.Id))
            .OrderBy(e => e.Timestamp)
            .ToList();
        if (ordered.Count == 0)
            return;

        var sigProps = opts.SignaturePropertyNames;
        var now = clock.UtcNow;
        var lastAlerted = state.SignatureLastAlerted;

        // Group fresh events by signature (alert at most once per signature this poll), keeping each
        // group's newest event as the representative. A signature alerts if it's brand-new, or if it
        // recurred and ReAlertAfter has elapsed since it last alerted.
        var toAlert = ordered
            .GroupBy(e => ErrorSignature.Compute(e, sigProps))
            .Select(g => new { Signature = g.Key, Latest = g.Last(), Count = g.Count() })
            .Where(g => ShouldAlert(g.Signature, lastAlerted, opts.ReAlertAfter, now))
            .ToList();

        int alertsSent = 0, suppressed = 0;
        foreach (var g in toAlert)
        {
            lastAlerted[g.Signature] = now; // record the alert time even if capped/undeliverable, so the cooldown holds

            if (alertsSent >= opts.MaxAlertsPerPoll)
            {
                suppressed++;
                continue;
            }

            var analysis = opts.AnalyzeWithCodex ? await analyzer.AnalyzeAsync(g.Latest, ct) : null;
            var text = ErrorAlert.Format(g.Latest, analysis, g.Count, sigProps);

            if (string.IsNullOrEmpty(ownerJid))
            {
                logger.LogInformation("Error alert (not delivered — no owner configured):\n{Text}", text);
                recorder.Record("error_alert", $"New error type {g.Latest.Id} (not delivered — no owner)", new { g.Latest.Id });
                alertsSent++;
            }
            else if (await sender.SendAsync(ownerJid, text, ct))
            {
                recorder.Record("error_alert", $"Alerted on new error type {g.Latest.Id}", new { g.Latest.Id });
                alertsSent++;
            }
            else
            {
                logger.LogWarning("Failed to deliver error alert for {Id}.", g.Latest.Id);
            }
        }

        if (suppressed > 0 && !string.IsNullOrEmpty(ownerJid))
            await sender.SendAsync(ownerJid, $"…and {suppressed} more new error type(s) this cycle (capped at {opts.MaxAlertsPerPoll}). Check Seq.", ct);

        // Commit dedup memory + advance the watermark.
        foreach (var e in ordered)
            state.SeenEventIds.Add(e.Id);
        var maxTs = ordered.Max(e => e.Timestamp);
        if (state.LastTimestampUtc is null || maxTs > state.LastTimestampUtc)
            state.LastTimestampUtc = maxTs;
        state.Trim();
        store.Save(state);

        logger.LogInformation(
            "Error-watch: {Alerted} alert(s) from {Total} fresh event(s); {Suppressed} capped.",
            alertsSent, ordered.Count, suppressed);
    }

    /// <summary>Alert if the signature is brand-new, or recurred after the re-alert cooldown elapsed.</summary>
    private static bool ShouldAlert(
        string signature, IReadOnlyDictionary<string, DateTimeOffset> lastAlerted, TimeSpan? reAlertAfter, DateTimeOffset now)
    {
        if (!lastAlerted.TryGetValue(signature, out var last))
            return true;
        return reAlertAfter is { } window && now - last >= window;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
