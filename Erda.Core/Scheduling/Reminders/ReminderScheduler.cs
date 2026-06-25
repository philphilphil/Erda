using Erda.Core.Abstractions;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Core.Scheduling;

/// <summary>
/// Background loop: every <c>Reminders:PollInterval</c>, read the reminder note and fire anything
/// due. Verbatim reminders are sent straight to Phil; scheduled prompts run through the agent (in a
/// fresh session) and the reply is sent. Recurring cadence + one-shot send-once are tracked in a
/// JSON sidecar so restarts neither replay nor double-fire. Mirrors <see cref="ErrorWatchScheduler"/>.
/// </summary>
public sealed class ReminderScheduler(
    IOptions<ReminderOptions> options,
    IOptions<WhatsAppOptions> whatsAppOptions,
    ReminderStore store,
    ReminderStateStore stateStore,
    VaultService vault,
    IAgentResponder responder,
    IPreScriptRunner scriptRunner,
    IWhatsAppSender sender,
    IClock clock,
    CurrentTimeContext timeContext,
    IActivityRecorder recorder,
    ILogger<ReminderScheduler> logger) : BackgroundService
{
    /// <summary>Placeholder a prompt can use to position the pre-run script's output.</summary>
    private const string ContextToken = "{{context}}";

    // Malformed rows already surfaced to Phil this process lifetime (so we don't re-nag every minute).
    private readonly HashSet<string> _notifiedMalformed = [];

    // Logged once per process when a row carries a script but pre-scripts are disabled (avoids spam).
    private bool _warnedPreScriptDisabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Reminder scheduler disabled (Reminders:Enabled=false).");
            return;
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(opts.TimeZone);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reminder scheduler: unknown TimeZone '{Tz}'; not starting.", opts.TimeZone);
            return;
        }

        var ownerJid = WhatsAppJid.FromNumber(whatsAppOptions.Value.OwnerNumber);
        if (string.IsNullOrEmpty(ownerJid))
            logger.LogWarning("Reminder scheduler: WhatsApp owner number not configured; reminders can't be delivered.");

        var state = stateStore.Load();

        logger.LogInformation("Reminder scheduler started: every {Interval}, zone {Tz}, note {Note}.",
            opts.PollInterval, opts.TimeZone, opts.NotePath);

        using var timer = new PeriodicTimer(opts.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(opts, stateStore, state, zone, ownerJid, clock.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reminder poll failed; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    /// <summary>One poll cycle. Exposed for testing (cf. <see cref="ErrorWatchScheduler.PollOnceAsync"/>).</summary>
    public async Task PollOnceAsync(
        ReminderOptions opts, ReminderStateStore stateStore, ReminderState state,
        TimeZoneInfo zone, string ownerJid, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var load = store.LoadAll();

        if (opts.NotifyOnError && !string.IsNullOrEmpty(ownerJid))
        {
            foreach (var bad in load.Malformed)
            {
                if (_notifiedMalformed.Add(bad))
                    await sender.SendAsync(ownerJid, $"⚠️ Couldn't parse a reminder row: {bad}", ct);
            }
        }

        var changed = false;
        foreach (var reminder in load.Reminders)
        {
            if (reminder.Status != ReminderStatus.Active)
                continue;
            try
            {
                changed |= reminder.Spec.IsRecurring
                    ? await EvaluateRecurringAsync(reminder, stateStore, state, zone, ownerJid, nowUtc, ct)
                    : await EvaluateOneShotAsync(reminder, opts, stateStore, state, zone, ownerJid, nowUtc, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reminder {Id} failed to evaluate.", reminder.Id);
            }
        }

        if (changed)
        {
            state.Trim();
            stateStore.Save(state);
        }
    }

    private async Task<bool> EvaluateRecurringAsync(
        Reminder r, ReminderStateStore stateStore, ReminderState state,
        TimeZoneInfo zone, string ownerJid, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (!state.LastFiredUtc.TryGetValue(r.Id, out var lastFired))
        {
            // First time we've seen this recurring reminder: seed without firing past occurrences.
            state.LastFiredUtc[r.Id] = nowUtc;
            stateStore.Save(state);
            return true;
        }

        var occurrence = r.Spec.Cron!.GetNextOccurrence(lastFired, zone);
        if (occurrence is not { } next || next > nowUtc)
            return false;

        // Advance to now (skipping any further missed occurrences → no backfill) and persist BEFORE
        // dispatch, so a crash during dispatch skips this occurrence rather than repeating it.
        state.LastFiredUtc[r.Id] = nowUtc;
        stateStore.Save(state);

        if (!await DispatchAsync(r, ownerJid, ct))
            await NotifyFailureAsync(r, ownerJid, ct);
        return true;
    }

    private async Task<bool> EvaluateOneShotAsync(
        Reminder r, ReminderOptions opts, ReminderStateStore stateStore, ReminderState state,
        TimeZoneInfo zone, string ownerJid, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (state.FiredOneShotIds.Contains(r.Id))
        {
            store.SetStatus(r.Id, ReminderStatus.Done); // heal the note if a prior write was lost
            return false;
        }

        var dueUtc = r.Spec.OneShotDueUtc(zone);
        if (nowUtc < dueUtc)
            return false;

        if (nowUtc - dueUtc > opts.OverdueGrace)
        {
            logger.LogInformation("Reminder {Id} is past its {Grace} grace window; marking done unfired.", r.Id, opts.OverdueGrace);
            state.FiredOneShotIds.Add(r.Id);
            stateStore.Save(state);
            store.SetStatus(r.Id, ReminderStatus.Done);
            return true;
        }

        if (await DispatchAsync(r, ownerJid, ct))
        {
            // Persist the send-once guard BEFORE the (visible-in-Obsidian) note write.
            state.FiredOneShotIds.Add(r.Id);
            stateStore.Save(state);
            store.SetStatus(r.Id, ReminderStatus.Done);
            return true;
        }

        await NotifyFailureAsync(r, ownerJid, ct); // leave active → retry next tick within grace
        return false;
    }

    /// <summary>Deliver a reminder: verbatim text, or the agent's reply to a prompt. Returns delivered.</summary>
    private async Task<bool> DispatchAsync(Reminder r, string ownerJid, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ownerJid))
        {
            logger.LogInformation("Reminder {Id} due but no owner configured; not delivered.", r.Id);
            return false;
        }

        if (r.Kind == ReminderKind.Reminder)
        {
            var delivered = await sender.SendAsync(ownerJid, r.Text, ct);
            if (delivered)
                recorder.Record("scheduled_fire", $"Reminder '{r.Id}' sent", new { r.Id, r.When });
            return delivered;
        }

        // Scheduled prompt: "@vault/path.md" uses that note's contents as the prompt; plain text
        // is used as-is. Lets a long prompt be maintained as a note rather than inline in the table.
        string promptText;
        try
        {
            promptText = ResolvePromptText(r.Text);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reminder {Id}: couldn't read prompt file for '{Text}'.", r.Id, r.Text);
            return false;
        }

        // Optional pre-run context script: run it and splice its stdout into the prompt. Sits before
        // the agent run, so its output is part of the prompt. Fail-safe: any script failure aborts
        // dispatch (→ NotifyFailureAsync) rather than running the model on missing context.
        if (!string.IsNullOrWhiteSpace(r.PreScript))
        {
            if (!options.Value.PreScriptEnabled)
            {
                if (!_warnedPreScriptDisabled)
                {
                    _warnedPreScriptDisabled = true;
                    logger.LogWarning(
                        "Pre-run scripts are disabled (Reminders:PreScriptEnabled=false); ignoring the script on '{Id}' and any others.",
                        r.Id);
                }
            }
            else
            {
                string context;
                try
                {
                    context = await scriptRunner.RunAsync(r.PreScript!, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Reminder {Id}: pre-run script failed.", r.Id);
                    return false;
                }
                promptText = InjectContext(promptText, context);
            }
        }

        var reply = await responder.RunOnceAsync([timeContext.Message(), new ChatMessage(ChatRole.User, promptText)], ct);
        var replyText = string.IsNullOrWhiteSpace(reply.Text) ? "(no response)" : reply.Text;
        var sent = await sender.SendAsync(ownerJid, $"⏰ {replyText}", ct);
        if (sent)
            recorder.Record("scheduled_fire", $"Prompt '{r.Id}' ran", new { r.Id, r.When, reply.ToolsUsed });
        return sent;
    }

    /// <summary>
    /// Splice the pre-run script's <paramref name="context"/> into <paramref name="promptText"/>:
    /// replace a literal <c>{{context}}</c> token if present, otherwise prepend a labelled block.
    /// </summary>
    internal static string InjectContext(string promptText, string context)
    {
        if (promptText.Contains(ContextToken, StringComparison.Ordinal))
            return promptText.Replace(ContextToken, context);
        return $"[Context gathered before this prompt]\n{context}\n\n{promptText}";
    }

    /// <summary>
    /// Resolve a scheduled prompt's text: "@path" → the contents of that vault file (relative to the
    /// vault root; the ".md" extension may be omitted). Plain text is returned unchanged.
    /// </summary>
    private string ResolvePromptText(string raw)
    {
        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('@'))
            return raw;

        var path = trimmed[1..].Trim();
        try
        {
            return vault.ReadNote(path);
        }
        catch (FileNotFoundException) when (path.Length > 0 && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return vault.ReadNote(path + ".md");
        }
    }

    private async Task NotifyFailureAsync(Reminder r, string ownerJid, CancellationToken ct)
    {
        if (options.Value.NotifyOnError && !string.IsNullOrEmpty(ownerJid))
            await sender.SendAsync(ownerJid, $"⚠️ Scheduled \"{r.Id}\" failed to run.", ct);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
