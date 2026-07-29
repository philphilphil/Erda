using Erda.Core.Abstractions;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Core.Scheduling;

/// <summary>
/// Executes and delivers a single reminder: a verbatim message is sent as-is; a scheduled prompt
/// resolves its text (inline or an <c>@vault/note.md</c>), runs the optional pre-run script, runs the
/// agent, and sends the reply to Phil over WhatsApp. Extracted from <see cref="ReminderScheduler"/> so
/// both the scheduler (on a due tick) and the panel's "run now" endpoint share one dispatch path.
///
/// This type owns ONLY execution + delivery — it never touches run-state or a reminder's status, so a
/// manual run has no effect on the schedule. The scheduler keeps all cadence/one-shot bookkeeping.
/// </summary>
public sealed class ReminderDispatcher(
    IAgentResponder responder,
    IWhatsAppSender sender,
    IPreScriptRunner scriptRunner,
    VaultService vault,
    CurrentTimeContext timeContext,
    IActivityRecorder recorder,
    IOptions<ReminderOptions> options,
    ILogger<ReminderDispatcher> logger)
{
    /// <summary>Placeholder a prompt can use to position the pre-run script's output.</summary>
    private const string ContextToken = "{{context}}";

    // Logged once per process when a row carries a script but pre-scripts are disabled (avoids spam).
    // int + Interlocked so "log once" still holds when a scheduler tick and a run-now overlap on this
    // shared singleton.
    private int _warnedPreScriptDisabled;

    /// <summary>
    /// Deliver a reminder: verbatim text, or the agent's reply to a prompt. Returns delivered.
    /// <paramref name="manual"/> flags a panel-triggered "run now" (only the activity record differs);
    /// either way the schedule is untouched.
    /// </summary>
    public async Task<bool> DispatchAsync(Reminder r, string ownerJid, bool manual, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ownerJid))
        {
            logger.LogInformation("Reminder {Id} due but no owner configured; not delivered.", r.Id);
            return false;
        }

        var tag = manual ? " (manual)" : "";

        if (r.Kind == ReminderKind.Reminder)
        {
            var delivered = await sender.SendAsync(ownerJid, r.Text, ct);
            if (delivered)
                recorder.Record("scheduled_fire", $"Reminder '{r.Id}' sent{tag}", new { r.Id, r.When, manual });
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
        // dispatch (→ caller notifies) rather than running the model on missing context.
        if (!string.IsNullOrWhiteSpace(r.PreScript))
        {
            if (!options.Value.PreScriptEnabled)
            {
                if (Interlocked.Exchange(ref _warnedPreScriptDisabled, 1) == 0)
                    logger.LogWarning(
                        "Pre-run scripts are disabled (Reminders:PreScriptEnabled=false); ignoring the script on '{Id}' and any others.",
                        r.Id);
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

        // An upstream model failure must not masquerade as "(no response)" — say so, and log it. The
        // send/record flow is unchanged: a failed turn is still delivered and still recorded.
        string replyText;
        if (reply.IsUpstreamFailure)
        {
            logger.LogWarning("Reminder {Id}: no response — upstream model failure (no text, no usage, no tools).", r.Id);
            replyText = "⚠️ The model didn't return anything (it may be overloaded).";
        }
        else
        {
            replyText = string.IsNullOrWhiteSpace(reply.Text) ? "(no response)" : reply.Text;
        }

        var sent = await sender.SendAsync(ownerJid, $"⏰ {replyText}", ct);
        if (sent)
            recorder.Record("scheduled_fire", $"Prompt '{r.Id}' ran{tag}", new { r.Id, r.When, reply.ToolsUsed, manual });
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
}
