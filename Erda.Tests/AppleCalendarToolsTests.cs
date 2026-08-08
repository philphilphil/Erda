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
        DateTimeOffset? end = null,
        string? startDay = null,
        string? endDay = null) =>
        new(calendar, title, null, start ?? Start, end ?? End, isAllDay, timeZone, startDay, endDay);

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
    public void Descriptions_name_the_real_apple_calendar_and_who_chooses_it()
    {
        var tools = Make(new FakeAppleBridgeClient());
        var create = Tool(tools, "create_calendar_event").Description;

        Assert.Contains("Apple Calendar", create);
        Assert.Contains("Calendar app on Phil's Mac", create);
        Assert.Contains("NOT Erda's own scheduler", create);
        // The model is told plainly that the choice is not its to make — and that asking about it
        // is not the workaround either.
        Assert.Contains("you do not choose a calendar", create, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not ask him which one", create, StringComparison.OrdinalIgnoreCase);
    }

    // The parameter is gone, not merely ignored: a model cannot pass what the schema does not
    // declare, which is a stronger guarantee than any wording in a description.
    [Fact]
    public void Create_has_no_calendar_parameter_at_all()
    {
        var schema = Tool(Make(new FakeAppleBridgeClient()), "create_calendar_event")
            .JsonSchema.ToString();

        Assert.DoesNotContain("\"calendar\"", schema);
        // The listing filter is untouched — reads still span, or narrow to, any calendar.
        Assert.Contains("\"calendar\"", Tool(Make(new FakeAppleBridgeClient()), "list_calendar_events")
            .JsonSchema.ToString());
    }

    [Fact]
    public async Task Create_passes_the_trimmed_title_and_times_through()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateEventResult = AppleBridgeResult<AppleCalendarEvent>.Ok(Event()),
        };
        var tool = Tool(Make(fake), "create_calendar_event");

        var result = await Invoke(tool, new Dictionary<string, object?>
        {
            ["title"] = "  Dentist  ",
            ["startAt"] = Start,
            ["endAt"] = End,
            ["notes"] = "bring the referral",
            ["timeZone"] = "Europe/Berlin",
        });

        var call = Assert.NotNull(fake.CreateEventCall);
        Assert.Equal("Dentist", call.Title);
        Assert.Equal(Start, call.StartAt);
        Assert.Equal(End, call.EndAt);
        Assert.Equal("bring the referral", call.Notes);
        Assert.Equal("Europe/Berlin", call.TimeZone);
        Assert.Contains("Created 'Dentist' in 'Privat'", result?.ToString());
    }

    // The caller named no calendar, so the response is the only way Phil learns where the
    // appointment went — which makes echoing it back load-bearing rather than decorative.
    [Fact]
    public async Task Create_reports_the_calendar_the_bridge_actually_wrote_to()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateEventResult = AppleBridgeResult<AppleCalendarEvent>.Ok(Event(calendar: "Familie")),
        };

        var result = await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["title"] = "Dentist",
            ["startAt"] = Start,
            ["endAt"] = End,
        });

        Assert.Contains("in 'Familie'", result?.ToString());
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
            ["title"] = "Dentist",
            ["startAt"] = Start,
            ["endAt"] = End,
        }))?.ToString();

        // 09:00 Berlin, not 07:00 UTC — and the end drops the repeated date.
        Assert.Contains("2026-08-03 09:00–10:00 Europe/Berlin", result);
    }

    [Fact]
    public async Task Create_refuses_an_empty_title_without_calling_the_bridge()
    {
        var fake = new FakeAppleBridgeClient();

        var result = await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["title"] = "",
            ["startAt"] = Start,
            ["endAt"] = End,
        });

        Assert.Contains("no title", result?.ToString());
        Assert.Null(fake.CreateEventCall);
    }

    // "No calendar chosen on the Mac" has to read as something Phil can act on, and specifically not
    // as "your Mac is unreachable" — the two are both failures of a create and mean entirely
    // different things.
    [Fact]
    public async Task Create_relays_an_unconfigured_write_calendar_as_a_thing_to_fix_on_the_mac()
    {
        var fake = new FakeAppleBridgeClient
        {
            CreateEventResult = AppleBridgeResult<AppleCalendarEvent>.Fail(
                "No calendar is set up for writing on the Mac — open the ErdaBridge app there and "
                + "choose which calendar events should go into."),
        };

        var result = (await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["title"] = "Dentist",
            ["startAt"] = Start,
            ["endAt"] = End,
        }))?.ToString();

        Assert.Contains("open the ErdaBridge app", result);
        Assert.DoesNotContain("unreachable", result);
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
                "The calendar ErdaBridge is set to write to is read-only"),
        };

        var result = await Invoke(Tool(Make(fake), "create_calendar_event"), new Dictionary<string, object?>
        {
            ["title"] = "Dentist",
            ["startAt"] = Start,
            ["endAt"] = End,
        });

        Assert.Contains("The calendar ErdaBridge is set to write to is read-only", result?.ToString());
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
    // midnight. This is the fallback path: an older bridge sends no days, so the instant is all
    // there is to go on.
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

    // The bug the days exist for: an all-day event is floating, so EventKit anchors it to the Mac's
    // zone and the wire carries only the instant behind it — 2026-08-10T22:00Z for a birthday
    // Calendar.app draws on Tuesday the 11th. Rendering the instant reports it a day early, every
    // time.
    [Fact]
    public async Task List_renders_an_all_day_event_on_the_day_the_bridge_states()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok(
            [
                Event(
                    title: "Opa's 85th Birthday",
                    isAllDay: true,
                    timeZone: null,
                    start: new DateTimeOffset(2026, 8, 10, 22, 0, 0, TimeSpan.Zero),
                    end: new DateTimeOffset(2026, 8, 11, 21, 59, 59, TimeSpan.Zero),
                    startDay: "2026-08-11",
                    endDay: "2026-08-11"),
            ]),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        Assert.Contains("all day on 2026-08-11", result);
        Assert.DoesNotContain("2026-08-10", result);
    }

    // A multi-day all-day event used to lose its end entirely — a week's holiday read as a single
    // day. The days are what make both ends sayable, and `endDay` is inclusive.
    [Fact]
    public async Task List_spans_both_days_of_a_multi_day_all_day_event()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok(
            [
                Event(
                    title: "Urlaub",
                    isAllDay: true,
                    timeZone: null,
                    start: new DateTimeOffset(2026, 8, 9, 22, 0, 0, TimeSpan.Zero),
                    end: new DateTimeOffset(2026, 8, 14, 21, 59, 59, TimeSpan.Zero),
                    startDay: "2026-08-10",
                    endDay: "2026-08-14"),
            ]),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        Assert.Contains("all day from 2026-08-10 to 2026-08-14", result);
    }

    // A create is always timed, and a timed event carries no days — so the day fields must not
    // leak into the ordinary rendering path.
    [Fact]
    public async Task A_timed_event_is_unaffected_by_the_day_fields()
    {
        var fake = new FakeAppleBridgeClient
        {
            ListEventsResult = AppleBridgeResult<IReadOnlyList<AppleCalendarEvent>>.Ok([Event()]),
        };

        var result = (await Invoke(Tool(Make(fake), "list_calendar_events"), []))?.ToString();

        Assert.Contains("2026-08-03 09:00–10:00 Europe/Berlin", result);
        Assert.DoesNotContain("all day", result);
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
