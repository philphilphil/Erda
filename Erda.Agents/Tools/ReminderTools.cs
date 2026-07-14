using System.ComponentModel;
using System.Text;
using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Agents.Tools;

/// <summary>
/// Agent tools for scheduling: <c>schedule_message</c> (sent verbatim at the time),
/// <c>schedule_prompt</c> (run through Erda, reply sent), plus <c>list_scheduled</c>,
/// <c>cancel_scheduled</c>, and <c>pause_scheduled</c>/<c>resume_scheduled</c>. All write to the
/// same DB table the scheduler reads, so Phil can also
/// edit them in the control panel. <c>when</c> is a date-time (once) or a cron expression (recurring),
/// interpreted in the configured timezone.
/// </summary>
public sealed class ReminderTools(ReminderStore store, VaultService vault, IOptions<ReminderOptions> options, IClock clock)
{
    private const string WhenHelp =
        "a date-time like '2026-06-15 09:00' (fires once) or a cron expression like '0 6 * * *' " +
        "(recurring; @daily/@weekly also work). Times are interpreted in the configured timezone.";

    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(ScheduleMessage, "schedule_message"),
        AIFunctionFactory.Create(SchedulePrompt, "schedule_prompt"),
        AIFunctionFactory.Create(ListScheduled, "list_scheduled"),
        AIFunctionFactory.Create(CancelScheduled, "cancel_scheduled"),
        AIFunctionFactory.Create(PauseScheduled, "pause_scheduled"),
        AIFunctionFactory.Create(ResumeScheduled, "resume_scheduled"),
    ];

    [Description("Schedule a message to be sent to Phil verbatim at a time (no AI at send time). " +
                 "Use for plain reminders like 'remind me to call mom'.")]
    public string ScheduleMessage(
        [Description("When to send it: " + WhenHelp)] string when,
        [Description("The exact text to send to Phil.")] string message,
        [Description("Optional short id; generated from the text if omitted.")] string? id = null)
        => Create(ReminderKind.Reminder, when, message, id);

    [Description("Schedule a prompt to run through Erda at a time; the reply is sent to Phil. " +
                 "Use for things needing live work, e.g. 'every morning, what's the weather?'.")]
    public string SchedulePrompt(
        [Description("When to run it: " + WhenHelp)] string when,
        [Description("The prompt Erda should run at that time. May instead be '@path/to/note.md' " +
                     "(from the vault root, .md optional) to run the contents of a vault note as the prompt.")] string prompt,
        [Description("Optional short id; generated from the text if omitted.")] string? id = null)
        => Create(ReminderKind.Prompt, when, prompt, id);

    [Description("List Phil's active and paused scheduled reminders and prompts, with their next run time.")]
    public string ListScheduled()
    {
        var rows = store.LoadAll().Reminders
            .Where(r => r.Status != ReminderStatus.Done)
            .ToList();
        if (rows.Count == 0)
            return "No scheduled reminders.";

        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            var kind = r.Kind == ReminderKind.Reminder ? "message" : "prompt";
            var paused = r.Status == ReminderStatus.Paused ? " [paused]" : "";
            sb.AppendLine($"• {r.Id} ({kind}){paused}: {r.Text} — when: {r.When}; next: {DescribeNext(r.Spec)}");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Cancel (delete) a scheduled reminder or prompt by its id.")]
    public string CancelScheduled(
        [Description("The id shown by list_scheduled.")] string id)
        => store.Remove(id.Trim()) ? $"Cancelled '{id.Trim()}'." : $"No scheduled item with id '{id.Trim()}'.";

    [Description("Pause a scheduled reminder or prompt by its id; it won't fire until resumed.")]
    public string PauseScheduled(
        [Description("The id shown by list_scheduled.")] string id)
        => store.SetStatus(id.Trim(), ReminderStatus.Paused)
            ? $"Paused '{id.Trim()}'. It won't fire until resumed."
            : $"No scheduled item with id '{id.Trim()}'.";

    [Description("Resume a paused scheduled reminder or prompt by its id.")]
    public string ResumeScheduled(
        [Description("The id shown by list_scheduled.")] string id)
        => store.SetStatus(id.Trim(), ReminderStatus.Active)
            ? $"Resumed '{id.Trim()}'."
            : $"No scheduled item with id '{id.Trim()}'.";

    private string Create(ReminderKind kind, string when, string text, string? id)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Cannot schedule empty text.";
        if (!WhenSpec.TryParse(when, out var spec))
            return $"I couldn't understand the schedule '{when}'. Use {WhenHelp}";

        var existing = store.LoadAll().Reminders.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalId = string.IsNullOrWhiteSpace(id) ? UniqueId(Slugify(text), existing) : id.Trim();

        store.Append(kind, finalId, when.Trim(), text.Trim());

        var what = kind == ReminderKind.Reminder ? "reminder" : "scheduled prompt";
        return $"Scheduled {what} '{finalId}'. Next: {DescribeNext(spec!)}.{FilePromptWarning(kind, text)}";
    }

    /// <summary>If a prompt references a vault file (@path) that doesn't exist yet, note it (non-blocking).</summary>
    private string FilePromptWarning(ReminderKind kind, string text)
    {
        var t = text.TrimStart();
        if (kind != ReminderKind.Prompt || !t.StartsWith('@'))
            return "";
        var path = t[1..].Trim();
        if (vault.Exists(path) || vault.Exists(path + ".md"))
            return "";
        return $" (note: I couldn't find '{path}' in the vault yet — create it before it runs.)";
    }

    private string DescribeNext(WhenSpec spec)
    {
        var zone = ResolveZone();
        if (spec.IsRecurring)
        {
            var occ = spec.Cron!.GetNextOccurrence(clock.UtcNow, zone);
            return occ is { } next ? $"{next:yyyy-MM-dd HH:mm} ({zone.Id})" : "never";
        }
        return $"{spec.OneShotLocal:yyyy-MM-dd HH:mm} ({zone.Id})";
    }

    private TimeZoneInfo ResolveZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static string UniqueId(string baseSlug, HashSet<string> existing)
    {
        if (!existing.Contains(baseSlug))
            return baseSlug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseSlug}-{n}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    private static string Slugify(string text)
    {
        var normalized = text.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var words = normalized
            .Split([' ', '-', '_', ',', '.', '!', '?', ':', ';', '(', ')', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Take(5)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0);
        var slug = string.Join("-", words);
        if (slug.Length > 24)
            slug = slug[..24].TrimEnd('-');
        return slug.Length > 0 ? slug : "reminder";
    }
}
