using System.ComponentModel;
using System.Globalization;
using System.Text;
using Erda.Core.Services;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// Agent tools for Apple Calendar, backed by the macOS ErdaBridge HTTP bridge
/// (<see cref="IAppleBridgeClient"/>) — the same LAN API on Phil's Mac that
/// <see cref="AppleReminderTools"/> uses, but a different capability with a different macOS
/// permission behind it.
/// <para>
/// Kept in its own file rather than folded into <see cref="AppleReminderTools"/> because the two
/// classes have to warn the model about different things. That class's whole job is to keep Apple
/// Reminders apart from Erda's own <c>schedule_*</c> scheduler; this one has to keep Apple Calendar
/// apart from that same scheduler <i>and</i> establish that "the calendar" means a real calendar in
/// Calendar.app named by its real title. One class carrying both sets of caveats would dilute both.
/// </para>
/// <para>
/// Two operations only — create an event and list upcoming ones. There is deliberately no edit, no
/// delete, no recurrence, no attendees and no alarms; see macos-bridge/README.md's threat model.
/// Registered on the agent only when <c>AppleBridge:Enabled</c> is true, alongside the reminder
/// tools.
/// </para>
/// <para>
/// <b>Reads span every calendar; writes go to exactly one.</b> <c>list_calendar_events</c> keeps its
/// optional calendar filter, but <c>create_calendar_event</c> has no calendar parameter at all — the
/// target is configured once by Phil in the ErdaBridge app on the Mac. That is deliberate: which
/// calendar an appointment belongs in was a decision the model had no good basis for, one more thing
/// it could be argued into getting wrong, and a reason it had to learn calendar names before it
/// could add anything. A parameter that does not exist is the only version of "never guess" that
/// cannot be talked around.
/// </para>
/// </summary>
public sealed class AppleCalendarTools(IAppleBridgeClient client)
{
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(CreateCalendarEvent, "create_calendar_event"),
        AIFunctionFactory.Create(ListCalendarEvents, "list_calendar_events"),
    ];

    [Description(
        "Create an event in Apple Calendar — the real Calendar app on Phil's Mac/iPhone, synced via " +
        "iCloud. This is NOT Erda's own scheduler: use schedule_message to be reminded about " +
        "something over WhatsApp, and use THIS for an actual appointment that belongs in his " +
        "calendar (a dentist appointment, a meeting, a train). The event goes into the one calendar " +
        "Phil configured on his Mac — you do not choose a calendar, do not need to know what his " +
        "calendars are called, and must not ask him which one to use. If he asks for a specific " +
        "calendar, tell him it is set once in the ErdaBridge app on the Mac. Start and end must be " +
        "full timestamps with a UTC offset, the end must be after the start, and an event cannot be " +
        "longer than 7 days. Events cannot be edited or deleted afterwards through Erda — say so if " +
        "Phil asks.")]
    private async Task<string> CreateCalendarEvent(
        [Description("The event's title, e.g. 'Dentist'.")] string title,
        [Description("Start as ISO-8601 with an explicit UTC offset or 'Z' (e.g. '2026-08-03T09:00:00+02:00'). " +
                     "A timestamp with no offset is rejected.")] DateTimeOffset startAt,
        [Description("End as ISO-8601 with an explicit UTC offset or 'Z'. Must be after the start and at most 7 days later.")] DateTimeOffset endAt,
        [Description("Optional notes/details for the event.")] string? notes = null,
        [Description("Optional IANA time zone the event displays in, e.g. 'Europe/Berlin'. Omit to use the Mac's zone. " +
                     "Abbreviations like 'CEST' or 'PST' are rejected.")] string? timeZone = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "Cannot create an event with no title.";
        if (endAt <= startAt)
            return "The end has to be after the start.";

        var result = await client.CreateCalendarEventAsync(
            title.Trim(), startAt, endAt, notes,
            string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim());

        if (!result.Success)
            return $"Couldn't create the event: {result.Error}";

        // The bridge reports which calendar the event landed in — the caller never named one, so
        // this is the only way to tell Phil where it went.
        var e = result.Value!;
        return $"Created '{e.Title}' in '{e.Calendar}', {FormatRange(e)}.";
    }

    [Description(
        "List upcoming events from Apple Calendar (the real Calendar app on Phil's Mac). This is NOT " +
        "Erda's own scheduled reminders/prompts — use list_scheduled for those. Reading spans every " +
        "calendar, unlike creating: the window starts now, and omitting the calendar covers all of " +
        "them. Only upcoming events are returned — there is no way to look at the past.")]
    private async Task<string> ListCalendarEvents(
        [Description("Optional: a specific calendar to filter to, named as it reads in Calendar.app. Omit to span every calendar.")] string? calendar = null,
        [Description("Optional window length in days, starting now (1–31; the bridge's default of 7 applies if omitted).")] int? days = null,
        [Description("Optional max number of events to return (the bridge's default applies if omitted).")] int? limit = null)
    {
        var calendars = string.IsNullOrWhiteSpace(calendar) ? null : new[] { calendar.Trim() };
        var result = await client.ListCalendarEventsAsync(calendars, days, limit);
        if (!result.Success)
            return $"Couldn't list calendar events: {result.Error}";

        var items = result.Value!;
        if (items.Count == 0)
            return $"Nothing in the calendar for the next {days ?? 7} days.{await AvailableCalendarsHintAsync()}";

        var sb = new StringBuilder();
        foreach (var e in items)
        {
            sb.Append("• ").Append(e.Title).Append(" (").Append(e.Calendar).Append(") — ").Append(FormatRange(e));
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Names the calendars that exist, appended only when a listing came back empty. That is exactly
    /// when the model needs them: an empty calendar never shows up in a listing, so without this
    /// there is no way to discover what a calendar is called in order to <i>filter</i> by it. It
    /// says nothing about where events are written — that is not a choice this class can make.
    /// Deliberately not a third tool, and deliberately not on the happy path — it costs an extra
    /// request. A failure here is swallowed: the caller already has its real answer.
    /// </summary>
    private async Task<string> AvailableCalendarsHintAsync()
    {
        var status = await client.GetStatusAsync();
        if (!status.Success || status.Value!.Calendars.Count == 0)
            return "";
        return $" Calendars on the Mac: {string.Join(", ", status.Value!.Calendars)}.";
    }

    /// <summary>
    /// Rendered in the event's own zone when it has one, so "09:00 Europe/Berlin" reads the way it
    /// does in Calendar.app rather than as the UTC instant behind it. An all-day event is named as
    /// such instead of being given a misleading midnight-to-midnight span.
    /// <para>
    /// An all-day event is rendered from the days the bridge states
    /// (<see cref="AppleCalendarEvent.StartDay"/>), never from its instants: it is a floating event,
    /// so the instants are anchored to the Mac's zone, that zone is not on the wire, and deriving a
    /// day from them here puts every birthday one day early. Only when they are absent — an older
    /// bridge — does this fall back to the instant, which is the behaviour that produced the bug and
    /// is kept solely so an un-updated bridge still says something.
    /// </para>
    /// </summary>
    private static string FormatRange(AppleCalendarEvent e)
    {
        var zone = ResolveZone(e.TimeZone);
        var start = TimeZoneInfo.ConvertTime(e.StartAt, zone);
        var end = TimeZoneInfo.ConvertTime(e.EndAt, zone);
        var label = e.TimeZone ?? "UTC";

        if (e.IsAllDay)
        {
            if (string.IsNullOrEmpty(e.StartDay))
                return $"all day on {start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

            // `EndDay` is the inclusive last day, so a single-day event repeats `StartDay` and is
            // named once. A multi-day one names both ends — the instant-based rendering dropped the
            // end entirely, turning a week's holiday into one day.
            return string.IsNullOrEmpty(e.EndDay) || e.EndDay == e.StartDay
                ? $"all day on {e.StartDay}"
                : $"all day from {e.StartDay} to {e.EndDay}";
        }

        // Same day: don't repeat the date on the end.
        var endFormat = start.Date == end.Date ? "HH:mm" : "yyyy-MM-dd HH:mm";
        return $"{start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}–"
             + $"{end.ToString(endFormat, CultureInfo.InvariantCulture)} {label}";
    }

    /// <summary>The bridge sends a canonical IANA identifier, but the host may not have that zone
    /// installed (or the event may be floating), so an unresolvable zone falls back to UTC rather
    /// than throwing inside a tool call. A floating event is always an all-day one, and its day now
    /// comes from <see cref="AppleCalendarEvent.StartDay"/> rather than from this fallback — which
    /// is what stopped a birthday being reported a day early.</summary>
    private static TimeZoneInfo ResolveZone(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(identifier);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
