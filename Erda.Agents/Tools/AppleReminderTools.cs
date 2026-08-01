using System.ComponentModel;
using System.Globalization;
using System.Text;
using Erda.Core.Services;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// Agent tools for Apple Reminders, backed by the macOS ErdaBridge HTTP bridge
/// (<see cref="IAppleBridgeClient"/>) — a small LAN API on Phil's Mac that creates/lists/completes
/// tasks in a handful of allowlisted Reminders lists, each identified by a short alias.
/// <para>
/// These are a genuinely different system from <see cref="ReminderTools"/>'
/// <c>schedule_message</c>/<c>schedule_prompt</c>/<c>list_scheduled</c>: those are Erda's own
/// DB-backed scheduler that sends a WhatsApp message or runs a prompt at a future time. THESE tools
/// create real to-do items in the Reminders app on Phil's Mac/iPhone (synced via iCloud). Only
/// registered on the agent when <c>AppleBridge:Enabled</c> is true.
/// </para>
/// </summary>
public sealed class AppleReminderTools(IAppleBridgeClient client)
{
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(CreateReminder, "create_apple_reminder"),
        AIFunctionFactory.Create(ListReminders, "list_apple_reminders"),
        AIFunctionFactory.Create(CompleteReminder, "complete_apple_reminder"),
    ];

    [Description(
        "Create a task in Apple Reminders (the Reminders app on Phil's Mac/iPhone, synced via iCloud) " +
        "in one of a small set of allowlisted lists. This is NOT Erda's own scheduler — do not use this " +
        "for 'remind me to call mom at 5pm' (that's schedule_message); use this for actual to-do items " +
        "Phil wants tracked in Reminders, e.g. 'add milk to my groceries list'. The list is named by a " +
        "short alias (e.g. 'groceries', 'work') — there is no default list, so ask Phil which list if " +
        "unsure, or call list_apple_reminders with no alias to see which ones are set up. An unknown or " +
        "no-longer-valid alias fails; never guess or fall back to a different list.")]
    private async Task<string> CreateReminder(
        [Description("The allowlisted Reminders list alias to add the task to, e.g. 'groceries'.")] string alias,
        [Description("The reminder's title, e.g. 'Buy milk'.")] string title,
        [Description("Optional notes/details for the reminder.")] string? notes = null,
        [Description("Optional due date/time as ISO-8601 with an explicit UTC offset or 'Z' " +
                     "(e.g. '2026-08-01T09:00:00Z'). Omit for no due date.")] DateTimeOffset? dueAt = null,
        [Description("Optional priority: 0 = none (default), 1 = highest … 9 = lowest.")] int? priority = null)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return "Tell me which Reminders list (alias) to add this to.";
        if (string.IsNullOrWhiteSpace(title))
            return "Cannot create a reminder with no title.";

        var result = await client.CreateReminderAsync(alias.Trim(), title.Trim(), notes, dueAt, priority);
        if (!result.Success)
            return $"Couldn't create the reminder: {result.Error}";

        var r = result.Value!;
        return $"Created '{r.Title}' in '{r.Alias}'" + (r.DueAt is { } due ? $", due {FormatDue(due)}." : ".");
    }

    [Description(
        "List Apple Reminders tasks (incomplete only) from one or more allowlisted lists. This is NOT " +
        "Erda's own scheduled reminders/prompts — use list_scheduled for those. Omit the alias to list " +
        "every healthy allowlisted list.")]
    private async Task<string> ListReminders(
        [Description("Optional: a specific Reminders list alias to filter to. Omit to list every allowlisted list.")] string? alias = null,
        [Description("Optional max number of reminders to return (the bridge's default applies if omitted).")] int? limit = null)
    {
        var aliases = string.IsNullOrWhiteSpace(alias) ? null : new[] { alias.Trim() };
        var result = await client.ListRemindersAsync(aliases, limit);
        if (!result.Success)
            return $"Couldn't list reminders: {result.Error}";

        var items = result.Value!;
        if (items.Count == 0)
            return "No Apple Reminders found.";

        var sb = new StringBuilder();
        foreach (var r in items)
        {
            sb.Append("• [").Append(r.Id).Append("] ").Append(r.Title).Append(" (").Append(r.Alias).Append(')');
            if (r.DueAt is { } due)
                sb.Append(" — due ").Append(FormatDue(due));
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    [Description(
        "Mark an Apple Reminders task as complete, by the id shown in list_apple_reminders (e.g. " +
        "'rem_...'). This is NOT Erda's own scheduler — use cancel_scheduled for those. Completing an " +
        "already-completed reminder succeeds as a no-op.")]
    private async Task<string> CompleteReminder(
        [Description("The reminder id shown by list_apple_reminders.")] string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "Tell me which reminder to complete.";

        var result = await client.CompleteReminderAsync(id.Trim());
        if (!result.Success)
            return $"Couldn't complete the reminder: {result.Error}";

        return result.Value!.AlreadyCompleted ? "That reminder was already completed." : "Marked as complete.";
    }

    private static string FormatDue(DateTimeOffset due) =>
        due.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
