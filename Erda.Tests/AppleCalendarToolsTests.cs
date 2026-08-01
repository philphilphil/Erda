using Erda.Agents.Tools;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class AppleCalendarToolsTests
{
    private static AppleCalendarTools Make(FakeAppleBridgeClient client) => new(client);

    private static AIFunction Tool(AppleCalendarTools tools, string name) =>
        (AIFunction)tools.AsTools().Single(t => ((AIFunction)t).Name == name);

    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset End = new(2026, 8, 3, 10, 0, 0, TimeSpan.FromHours(2));

    private static AppleCalendarEvent Event(
        string calendar = "Privat",
        string title = "Dentist",
        bool isAllDay = false,
        string? timeZone = "Europe/Berlin",
        DateTimeOffset? start = null,
        DateTimeOffset? end = null) =>
        new(calendar, title, null, start ?? Start, end ?? End, isAllDay, timeZone);

    private static ValueTask<object?> Invoke(AIFunction tool, Dictionary<string, object?> args) =>
        tool.InvokeAsync(new AIFunctionArguments(args));

    [Fact]
    public void Exposes_the_two_apple_calendar_tools()
    {
        var names = Make(new FakeAppleBridgeClient()).AsTools().Select(t => ((AIFunction)t).Name).ToList();

        Assert.Equal(["create_calendar_event", "list_calendar_events"], names);
    }

    // The tool descriptions carry the whole of what stops the model confusing three different
    // "calendars": Apple Calendar, Erda's own scheduler, and Google Calendar over MCP.
    [Fact]
    public void Descriptions_name_the_real_apple_calendar_and_how_to_address_it()
    {
        var tools = Make(new FakeAppleBridgeClient());
        var create = Tool(tools, "create_calendar_event").Description;

        Assert.Contains("Apple Calendar", create);
        Assert.Contains("Calendar.app", create);
        Assert.Contains("NOT Erda's own scheduler", create);
        // The name-by-title contract, and that guessing is not allowed.
        Assert.Contains("real title", create);
        Assert.Contains("never guess", create, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_passes_the_trimmed_calendar_title_and_times_through()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateEventResult = AppleBridgeResult<AppleCalendarEvent>.Ok(Event()),
        };
        var tool = Tool(Make(fake), "create_calendar_event");

        var result = await Invoke(tool, new Dictionary<string, object?>
        {
            ["calendar"] = "  Privat  ",
            ["title"] = "  Dentist  ",
            ["startAt"] = Start,
            ["endAt"] = End,
            ["notes"] = "bring the referral",
            ["timeZone"] = "Europe/Berlin",
        });

        var call = Assert.NotNull(fake.CreateEventCall);
        Assert.Equal("Privat", call.Calendar);
        Assert.Equal("Dentist", call.Title);
        Assert.Equal(Start, call.StartAt);
        Assert.Equal(End, call.EndAt);
        Assert.Equal("bring the referral", call.Notes);
        Assert.Equal("Europe/Berlin", call.TimeZone);
        Assert.Contains("Created 'Dentist' in 'Privat'", result?.ToString());
    }

    // The bridge echoes the calendar's own spelling back, so a case-insensitive match reports where
    // the event actually landed rather than what was asked for.
    [Fact]
    public async Task Create_reports_the_calendar_the_bridge_actually_used()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateEventResult = AppleBridgeResult<AppleCalendarEvent>.Ok(Event(calendar: "Privat")),
        };

        var result = await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["calendar"] = "privat",
            ["title"] = "Dentist",
            ["startAt"] = Start,
            ["endAt"] = End,
        });

        Assert.Equal("privat", Assert.NotNull(fake.CreateEventCall).Calendar);
        Assert.Contains("in 'Privat'", result?.ToString());
    }

    // The event is rendered in its own zone, so what comes back reads the way it does in
    // Calendar.app rather than as the UTC instant behind it.
    [Fact]
    public async Task Create_renders_the_time_in_the_events_own_zone()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateEventResult = AppleBridgeResult<AppleCalendarEvent>.Ok(Event()),
        };

        var result = (await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["calendar"] = "Privat",
            ["title"] = "Dentist",
            ["startAt"] = Start,
            ["endAt"] = End,
        }))?.ToString();

        // 09:00 Berlin, not 07:00 UTC — and the end drops the repeated date.
        Assert.Contains("2026-08-03 09:00–10:00 Europe/Berlin", result);
    }

    [Theory]
    [InlineData("", "Dentist", "which calendar")]
    [InlineData("Privat", "", "no title")]
    public async Task Create_refuses_an_empty_calendar_or_title_without_calling_the_bridge(
        string calendar, string title, string expected)
    {
        var fake = new FakeAppleBridgeClient();

        var result = await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["calendar"] = calendar,
            ["title"] = title,
            ["startAt"] = Start,
            ["endAt"] = End,
        });

        Assert.Contains(expected, result?.ToString());
        Assert.Null(fake.CreateEventCall);
    }

    // Caught here rather than at the bridge: an obviously inverted interval is a wasted round trip,
    // and the message can say what is wrong where the bridge's closed error set cannot.
    [Fact]
    public async Task Create_refuses_an_end_at_or_before_the_start_without_calling_the_bridge()
    {
        var fake = new FakeAppleBridgeClient();

        foreach (var end in new[] { Start, Start.AddHours(-1) })
        {
            var result = await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
            {
                ["calendar"] = "Privat",
                ["title"] = "Dentist",
                ["startAt"] = Start,
                ["endAt"] = end,
            });

            Assert.Contains("after the start", result?.ToString());
        }
        Assert.Null(fake.CreateEventCall);
    }

    // Every bridge failure is relayed verbatim — each one implies a different fix, and rewriting
    // them into one "couldn't create it" would throw that away.
    [Fact]
    public async Task Create_relays_the_bridge_error_verbatim()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateEventResult = AppleBridgeResult<AppleCalendarEvent>.Fail(
                "Two calendars on the Mac have that exact name"),
        };

        var result = await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["calendar"] = "Privat",
            ["title"] = "Dentist",
            ["startAt"] = Start,
            ["endAt"] = End,
        });

        Assert.Contains("Two calendars on the Mac have that exact name", result?.ToString());
    }

    [Fact]
    public async Task List_passes_the_filter_and_window_through()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok([Event()]),
        };

        var result = await Invoke(Tool(Make(fake), "list_calendar_events"), new Dictionary<string, object?>
        {
            ["calendar"] = "  Privat  ",
            ["days"] = 14,
            ["limit"] = 25,
        });

        var call = Assert.NotNull(fake.ListEventsCall);
        Assert.Equal(["Privat"], call.Calendars);
        Assert.Equal(14, call.Days);
        Assert.Equal(25, call.Limit);
        Assert.Contains("Dentist (Privat)", result?.ToString());
    }

    [Fact]
    public async Task List_without_a_calendar_spans_every_calendar()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok([Event()]),
        };

        await Invoke(Tool(Make(fake), "list_calendar_events"), []);

        Assert.Null(Assert.NotNull(fake.ListEventsCall).Calendars);
    }

    // An all-day event has no meaningful clock time, and reporting one would put a birthday at
    // midnight — or, east of London, on the wrong day.
    [Fact]
    public async Task List_names_an_all_day_event_as_such_rather_than_giving_it_a_time()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok(
                [Event(title: "Birthday", isAllDay: true, timeZone: null)]),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        Assert.Contains("all day on 2026-08-03", result);
        Assert.DoesNotContain("09:00", result);
    }

    [Fact]
    public async Task List_spans_days_when_an_event_crosses_midnight()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok(
                [Event(title: "Night train", start: Start, end: Start.AddHours(20))]),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        Assert.Contains("2026-08-03 09:00–2026-08-04 05:00 Europe/Berlin", result);
    }

    // An empty calendar never appears in a listing, so an empty result is exactly when the model
    // needs to be told what the calendars are called. The extra status call happens only here.
    [Fact]
    public async Task An_empty_listing_names_the_calendars_that_exist()
    {
        var fake = new FakeAppleBridgeClient
        {
            StatusResult = AppleBridgeResult<AppleBridgeStatus>.Ok(
                new AppleBridgeStatus("ok", ["Groceries"], "ok", ["Arbeit", "Privat"])),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        Assert.Contains("Nothing in the calendar for the next 7 days.", result);
        Assert.Contains("Calendars on the Mac: Arbeit, Privat.", result);
        Assert.Equal(1, fake.StatusCallCount);
    }

    [Fact]
    public async Task A_non_empty_listing_does_not_pay_for_a_status_call()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok([Event()]),
        };

        await Invoke(Tool(Make(fake), "list_calendar_events"), []);

        Assert.Equal(0, fake.StatusCallCount);
    }

    // The hint is a nicety; a bridge that cannot answer the status call must not turn a perfectly
    // good "nothing scheduled" into an error.
    [Fact]
    public async Task An_empty_listing_still_answers_when_the_status_call_fails()
    {
        var fake = new FakeAppleBridgeClient
        {
            StatusResult = AppleBridgeResult<AppleBridgeStatus>.Fail("Couldn't reach the ErdaBridge app"),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        Assert.Contains("Nothing in the calendar", result);
        Assert.DoesNotContain("Couldn't reach", result);
    }

    [Fact]
    public async Task List_relays_the_bridge_error_verbatim()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Fail(
                "The Mac has revoked (or never granted) Calendar access"),
        };

        var result = await Invoke(Tool(Make(fake), "list_calendar_events"), []);

        Assert.Contains("The Mac has revoked (or never granted) Calendar access", result?.ToString());
    }

    // A tool that threw would blow up the agent turn; every path has to come back as text.
    [Fact]
    public async Task An_unresolvable_time_zone_falls_back_to_utc_instead_of_throwing()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok(
                [Event(timeZone: "Mars/Olympus_Mons")]),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        // 07:00 UTC, the instant behind 09:00 Berlin, labelled with what the bridge reported.
        Assert.Contains("2026-08-03 07:00–08:00 Mars/Olympus_Mons", result);
    }
}
