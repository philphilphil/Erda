using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class AppleBridgeClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public object? ResponseBody { get; set; }
        public Exception? ThrowOnSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (ThrowOnSend is not null)
                throw ThrowOnSend;

            var response = new HttpResponseMessage(Status);
            if (ResponseBody is not null)
                response.Content = JsonContent.Create(ResponseBody);
            return response;
        }
    }

    private static AppleBridgeClient Make(CapturingHandler handler, string baseUrl = "http://192.168.1.50:17832", string apiKey = "tok3n") =>
        new(new HttpClient(handler),
            Options.Create(new AppleBridgeOptions { Enabled = true, BaseUrl = baseUrl, ApiKey = apiKey, TimeoutSeconds = 5 }),
            NullLogger<AppleBridgeClient>.Instance);

    private static readonly object StatusOk = new
    {
        availability = "ok",
        lists = new[] { "Groceries" },
        calendarAvailability = "ok",
        calendars = new[] { "Privat" },
    };

    private static object EventBody(string calendar = "Privat", string title = "Dentist") => new
    {
        calendar,
        title,
        notes = (string?)null,
        startAt = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(2)),
        endAt = new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.FromHours(2)),
        isAllDay = false,
        timeZone = "Europe/Berlin",
    };

    private static object ReminderBody(string list = "Groceries", string title = "Buy milk") => new
    {
        id = "rem_11111111-1111-1111-1111-111111111111",
        list,
        title,
        notes = (string?)null,
        dueAt = (DateTimeOffset?)null,
        priority = 0,
        isCompleted = false,
        completedAt = (DateTimeOffset?)null,
    };

    [Fact]
    public async Task Sends_bearer_auth_header_on_every_call_including_status()
    {
        var handler = new CapturingHandler { ResponseBody = StatusOk };
        var client = Make(handler, apiKey: "s3cr3t");

        var result = await client.GetStatusAsync();

        Assert.True(result.Success);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("s3cr3t", handler.Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Create_reminder_sends_a_fresh_guid_idempotency_key()
    {
        var handler = new CapturingHandler { ResponseBody = ReminderBody() };
        var client = Make(handler);

        var result = await client.CreateReminderAsync("Groceries", "Buy milk");

        Assert.True(result.Success);
        Assert.True(handler.Request!.Headers.Contains("Idempotency-Key"));
        Assert.True(Guid.TryParse(handler.Request.Headers.GetValues("Idempotency-Key").Single(), out _));
    }

    [Fact]
    public async Task Complete_reminder_sends_a_fresh_guid_idempotency_key()
    {
        var handler = new CapturingHandler { ResponseBody = new { id = "rem_11111111-1111-1111-1111-111111111111", alreadyCompleted = false } };
        var client = Make(handler);

        await client.CompleteReminderAsync("rem_11111111-1111-1111-1111-111111111111");

        Assert.True(handler.Request!.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task List_and_status_do_not_send_an_idempotency_key()
    {
        var listHandler = new CapturingHandler { ResponseBody = new { items = Array.Empty<object>() } };
        await Make(listHandler).ListRemindersAsync();
        Assert.False(listHandler.Request!.Headers.Contains("Idempotency-Key"));

        var statusHandler = new CapturingHandler { ResponseBody = StatusOk };
        await Make(statusHandler).GetStatusAsync();
        Assert.False(statusHandler.Request!.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task List_reminders_unwraps_the_items_object_and_builds_a_repeated_list_query()
    {
        var handler = new CapturingHandler
        {
            ResponseBody = new { items = new[] { ReminderBody() } },
        };
        var client = Make(handler);

        var result = await client.ListRemindersAsync(["Groceries", "Work"], limit: 10);

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal("Buy milk", result.Value![0].Title);
        Assert.Equal("Groceries", result.Value![0].List);
        // AbsoluteUri, not ToString(): ToString() unescapes for display, so it cannot see whether
        // the request line is actually escaped. AbsoluteUri is what HttpClient writes.
        Assert.Equal(
            "http://192.168.1.50:17832/v1/reminders?list=Groceries&list=Work&limit=10",
            handler.Request!.RequestUri!.AbsoluteUri);
    }

    // Lists are addressed by their real name now, so the query has to survive spaces and non-ASCII.
    // A raw space would break the request line itself, and the bridge does not treat "+" as a
    // space — so a space has to go out as %20 and nothing else.
    [Fact]
    public async Task List_reminders_percent_encodes_a_name_with_spaces_and_non_ascii()
    {
        var handler = new CapturingHandler { ResponseBody = new { items = Array.Empty<object>() } };

        await Make(handler).ListRemindersAsync(["To Do", "Einkäufe"]);

        var uri = handler.Request!.RequestUri!;
        Assert.Equal("/v1/reminders?list=To%20Do&list=Eink%C3%A4ufe", uri.PathAndQuery);
        Assert.DoesNotContain(" ", uri.PathAndQuery);
        Assert.DoesNotContain("+", uri.PathAndQuery);
    }

    [Fact]
    public async Task Status_reports_the_list_names_the_bridge_can_reach()
    {
        var handler = new CapturingHandler
        {
            ResponseBody = new
            {
                availability = "ok",
                lists = new[] { "Groceries", "Work" },
                calendarAvailability = "ok",
                calendars = new[] { "Arbeit", "Privat" },
            },
        };

        var result = await Make(handler).GetStatusAsync();

        Assert.True(result.Success);
        Assert.Equal("ok", result.Value!.Availability);
        Assert.Equal(["Groceries", "Work"], result.Value!.Lists);
        Assert.Equal(["Arbeit", "Privat"], result.Value!.Calendars);
    }

    // macOS grants the two permissions separately, so the bridge reports them separately — a client
    // that collapsed them would tell Phil to fix the wrong setting.
    [Fact]
    public async Task Status_reports_calendar_availability_separately_from_reminders()
    {
        var handler = new CapturingHandler
        {
            ResponseBody = new
            {
                availability = "ok",
                lists = new[] { "Groceries" },
                calendarAvailability = "unauthorized",
                calendars = Array.Empty<string>(),
            },
        };

        var result = await Make(handler).GetStatusAsync();

        Assert.True(result.Success);
        Assert.Equal("ok", result.Value!.Availability);
        Assert.Equal("unauthorized", result.Value!.CalendarAvailability);
        Assert.Empty(result.Value!.Calendars);
    }

    // The calendars a listing may filter by are not where events go. Status reports both, and the
    // write target is the only way Erda can explain a `calendar_not_configured` to Phil.
    [Fact]
    public async Task Status_reports_the_write_calendar_separately_from_the_readable_ones()
    {
        var handler = new CapturingHandler
        {
            ResponseBody = new
            {
                availability = "ok",
                lists = new[] { "Groceries" },
                calendarAvailability = "ok",
                calendars = new[] { "Arbeit", "Privat" },
                writeCalendar = new { state = "ok", name = "Privat" },
            },
        };

        var result = await Make(handler).GetStatusAsync();

        Assert.True(result.Success);
        Assert.Equal(["Arbeit", "Privat"], result.Value!.Calendars);
        Assert.Equal("ok", result.Value!.WriteCalendar!.State);
        Assert.Equal("Privat", result.Value!.WriteCalendar!.Name);
    }

    // Never chosen carries no name at all, so the field is absent rather than empty — a client that
    // read "" as a calendar title would report a nonsense one.
    [Fact]
    public async Task Status_reports_an_unchosen_write_calendar_without_inventing_a_name()
    {
        var handler = new CapturingHandler
        {
            ResponseBody = new
            {
                availability = "ok",
                lists = Array.Empty<string>(),
                calendarAvailability = "ok",
                calendars = new[] { "Privat" },
                writeCalendar = new { state = "not_configured" },
            },
        };

        var result = await Make(handler).GetStatusAsync();

        Assert.True(result.Success);
        Assert.Equal("not_configured", result.Value!.WriteCalendar!.State);
        Assert.Null(result.Value!.WriteCalendar!.Name);
    }

    [Fact]
    public async Task Create_reminder_round_trips_a_non_utc_offset_due_date()
    {
        // 2026-08-01T11:00:00+02:00 — a non-UTC, non-zero offset, so a bug that normalises to UTC or
        // (worse) silently drops the offset by deserialising into a plain DateTime would show up here.
        var due = new DateTimeOffset(2026, 8, 1, 11, 0, 0, TimeSpan.FromHours(2));
        var handler = new CapturingHandler
        {
            ResponseBody = new
            {
                id = "rem_11111111-1111-1111-1111-111111111111",
                list = "Groceries",
                title = "Buy milk",
                notes = (string?)null,
                dueAt = due, // the bridge would normally echo this back as UTC "Z" (ISO8601.string);
                             // using the original offset here isolates the client's own (de)serialization.
                priority = 0,
                isCompleted = false,
                completedAt = (DateTimeOffset?)null,
            },
        };
        var client = Make(handler);

        var result = await client.CreateReminderAsync("Groceries", "Buy milk", dueAt: due);

        Assert.True(result.Success);

        // The bridge rejects an offset-less dueAt outright (ISO8601.parseRequiringOffset), so the
        // request body must carry an explicit, non-"Z" offset — not a naive or UTC-normalised timestamp.
        using var sentBody = JsonDocument.Parse(handler.Body!);
        Assert.Contains("+02:00", sentBody.RootElement.GetProperty("dueAt").GetString());

        // DueAt is a DateTimeOffset, not a DateTime, so the response deserializer can't silently drop
        // the offset either — the round trip must land on the exact same point in time.
        Assert.Equal(due, result.Value!.DueAt);
    }

    [Theory]
    [InlineData("invalid_request")]
    [InlineData("unauthorized")]
    [InlineData("not_found")]
    [InlineData("payload_too_large")]
    [InlineData("rate_limited")]
    [InlineData("idempotency_key_reuse")]
    [InlineData("request_in_progress")]
    [InlineData("no_such_list")]
    [InlineData("list_read_only")]
    [InlineData("reminders_unavailable")]
    [InlineData("no_such_calendar")]
    [InlineData("ambiguous_calendar")]
    [InlineData("calendar_read_only")]
    [InlineData("calendar_unavailable")]
    [InlineData("internal")]
    public async Task Every_closed_error_code_maps_to_a_non_empty_message(string code)
    {
        var handler = new CapturingHandler { Status = HttpStatusCode.BadRequest, ResponseBody = new { error = code, requestId = "req-1" } };
        var result = await Make(handler).CreateReminderAsync("Groceries", "Buy milk");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // The two list-side failures call for different fixes — use a different name vs. use a
    // different list — so they must not read the same.
    [Fact]
    public async Task No_such_list_and_list_read_only_produce_visibly_different_messages()
    {
        var missing = await Make(new CapturingHandler { Status = HttpStatusCode.NotFound, ResponseBody = new { error = "no_such_list", requestId = "r1" } })
            .CreateReminderAsync("Nope", "x");
        var readOnly = await Make(new CapturingHandler { Status = HttpStatusCode.Conflict, ResponseBody = new { error = "list_read_only", requestId = "r2" } })
            .CreateReminderAsync("Shared", "x");

        Assert.NotEqual(missing.Error, readOnly.Error);
        Assert.Contains("no Reminders list with that name", missing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", readOnly.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reminders_unavailable_names_the_macos_permission_not_the_list()
    {
        var result = await Make(new CapturingHandler { Status = HttpStatusCode.ServiceUnavailable, ResponseBody = new { error = "reminders_unavailable", requestId = "r1" } })
            .CreateReminderAsync("Groceries", "x");

        Assert.False(result.Success);
        Assert.Contains("Reminders permission", result.Error);
        Assert.DoesNotContain("no Reminders list with that name", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_error_code_still_yields_a_readable_message()
    {
        var result = await Make(new CapturingHandler { Status = (HttpStatusCode)599, ResponseBody = new { error = "something_new", requestId = "r1" } })
            .CreateReminderAsync("Groceries", "x");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task Transport_exception_yields_a_failure_result_instead_of_throwing()
    {
        var handler = new CapturingHandler { ThrowOnSend = new HttpRequestException("connection refused") };
        var client = Make(handler);

        var result = await client.CreateReminderAsync("Groceries", "Buy milk");

        Assert.False(result.Success);
        Assert.Contains("Couldn't reach", result.Error);
    }

    [Fact]
    public async Task Unconfigured_base_url_fails_without_making_a_call()
    {
        var handler = new CapturingHandler();
        var client = new AppleBridgeClient(
            new HttpClient(handler),
            Options.Create(new AppleBridgeOptions { BaseUrl = "", ApiKey = "x" }),
            NullLogger<AppleBridgeClient>.Instance);

        var result = await client.CreateReminderAsync("Groceries", "Buy milk");

        Assert.False(result.Success);
        Assert.Null(handler.Request);
    }

    /// <summary>
    /// A handler that yields before touching the request, so the caller's `using` scope has
    /// definitely exited by the time the body is read — which is what a real socket does and an
    /// in-memory fake does not.
    /// </summary>
    private sealed class YieldingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public object? ResponseBody { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (request.Content is not null)
            {
                // CopyToAsync is what HttpConnection actually calls, and it is the call that throws
                // ObjectDisposedException on a disposed body. ReadAsStringAsync does not, which is
                // why a fake built on it cannot see this class of bug.
                using var buffer = new MemoryStream();
                await request.Content.CopyToAsync(buffer, cancellationToken);
                Body = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ResponseBody) };
        }
    }

    private static AppleBridgeClient MakeYielding(YieldingHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new AppleBridgeOptions { Enabled = true, BaseUrl = "http://192.168.1.50:17832", ApiKey = "tok3n", TimeoutSeconds = 5 }),
            NullLogger<AppleBridgeClient>.Instance);

    // Regression: the request was built with `using var request = ...` and the send Task returned
    // without `await`, so the request — and its JsonContent — were disposed before HttpClient wrote
    // them to the socket. Against a real bridge that surfaced as ObjectDisposedException wrapped in
    // HttpRequestException, i.e. indistinguishable from "the Mac is asleep". The in-memory fake
    // completed synchronously and hid it; yielding first reproduces the real ordering.
    [Fact]
    public async Task Create_reminder_body_survives_an_async_gap_before_it_is_read()
    {
        var handler = new YieldingHandler { ResponseBody = ReminderBody() };

        var result = await MakeYielding(handler).CreateReminderAsync("Groceries", "Buy milk");

        Assert.True(result.Success);
        Assert.NotNull(handler.Body);
        Assert.Contains("Buy milk", handler.Body);
    }

    [Fact]
    public async Task Complete_and_status_also_survive_an_async_gap()
    {
        var complete = new YieldingHandler
        {
            ResponseBody = new { id = "rem_11111111-1111-1111-1111-111111111111", alreadyCompleted = false },
        };
        Assert.True((await MakeYielding(complete).CompleteReminderAsync("rem_11111111-1111-1111-1111-111111111111")).Success);

        var status = new YieldingHandler { ResponseBody = StatusOk };
        Assert.True((await MakeYielding(status).GetStatusAsync()).Success);
    }

    // The same regression, on the new POST. `using var request` plus a returned-but-not-awaited send
    // disposes the JsonContent before HttpClient writes it, and the failure looks exactly like "the
    // Mac is asleep". The in-memory fake completes synchronously and hides it; yielding first
    // reproduces the real ordering.
    [Fact]
    public async Task Create_calendar_event_body_survives_an_async_gap_before_it_is_read()
    {
        var handler = new YieldingHandler { ResponseBody = EventBody() };

        var result = await MakeYielding(handler).CreateCalendarEventAsync(
            "Dentist",
            new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.FromHours(2)));

        Assert.True(result.Success);
        Assert.NotNull(handler.Body);
        Assert.Contains("Dentist", handler.Body);
        Assert.Contains("2026-08-03T09:00:00+02:00", handler.Body);
    }

    // MARK: - Calendar events

    [Fact]
    public async Task Create_calendar_event_posts_to_the_calendar_route_with_an_idempotency_key()
    {
        var handler = new CapturingHandler { ResponseBody = EventBody() };

        var result = await Make(handler).CreateCalendarEventAsync(
            "Dentist",
            new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.FromHours(2)),
            notes: "bring the referral",
            timeZone: "Europe/Berlin");

        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/v1/calendar-events", handler.Request.RequestUri!.AbsolutePath);
        Assert.True(Guid.TryParse(handler.Request.Headers.GetValues("Idempotency-Key").Single(), out _));

        using var sentBody = JsonDocument.Parse(handler.Body!);
        // No calendar goes out at all: the write target lives on the Mac, and the bridge decodes
        // this body strictly — sending one would be a 400, not a preference it quietly ignores.
        Assert.False(sentBody.RootElement.TryGetProperty("calendar", out _));
        Assert.Equal("Dentist", sentBody.RootElement.GetProperty("title").GetString());
        Assert.Equal("bring the referral", sentBody.RootElement.GetProperty("notes").GetString());
        Assert.Equal("Europe/Berlin", sentBody.RootElement.GetProperty("timeZone").GetString());
    }

    // The bridge refuses a timestamp with no offset outright (ISO8601.parseRequiringOffset), so both
    // ends have to go out carrying one — and it must be the caller's offset, not a UTC-normalised
    // rewrite, or the event displays at the wrong wall-clock time.
    [Fact]
    public async Task Create_calendar_event_sends_both_timestamps_with_their_explicit_offset()
    {
        var handler = new CapturingHandler { ResponseBody = EventBody() };
        var start = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2026, 8, 3, 10, 30, 0, TimeSpan.FromHours(2));

        await Make(handler).CreateCalendarEventAsync("Dentist", start, end);

        using var sentBody = JsonDocument.Parse(handler.Body!);
        Assert.Contains("+02:00", sentBody.RootElement.GetProperty("startAt").GetString());
        Assert.Contains("+02:00", sentBody.RootElement.GetProperty("endAt").GetString());
    }

    // DateTimeOffset, not DateTime, so the response deserializer cannot silently drop the offset
    // either — the round trip has to land on the same instant.
    [Fact]
    public async Task Create_calendar_event_round_trips_a_non_utc_offset()
    {
        var start = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(2));
        var handler = new CapturingHandler { ResponseBody = EventBody() };

        var result = await Make(handler).CreateCalendarEventAsync(
            "Dentist", start, start.AddHours(1));

        Assert.True(result.Success);
        Assert.Equal(start, result.Value!.StartAt);
        Assert.Equal("Europe/Berlin", result.Value!.TimeZone);
        Assert.False(result.Value!.IsAllDay);
    }

    [Fact]
    public async Task List_calendar_events_unwraps_the_items_object_and_builds_its_query()
    {
        var handler = new CapturingHandler { ResponseBody = new { items = new[] { EventBody() } } };

        var result = await Make(handler).ListCalendarEventsAsync(["Privat", "Arbeit"], days: 14, limit: 25);

        Assert.True(result.Success);
        Assert.Single(result.Value!);
        Assert.Equal("Dentist", result.Value![0].Title);
        Assert.Equal(
            "http://192.168.1.50:17832/v1/calendar-events?calendar=Privat&calendar=Arbeit&days=14&limit=25",
            handler.Request!.RequestUri!.AbsoluteUri);
    }

    // Calendar names hold spaces and umlauts just like list names, and the bridge does not treat "+"
    // as a space — so a space has to go out as %20 and nothing else.
    [Fact]
    public async Task List_calendar_events_percent_encodes_a_name_with_spaces_and_non_ascii()
    {
        var handler = new CapturingHandler { ResponseBody = new { items = Array.Empty<object>() } };

        await Make(handler).ListCalendarEventsAsync(["Family / Shared", "Geburtstage ☕"]);

        var uri = handler.Request!.RequestUri!;
        Assert.DoesNotContain(" ", uri.PathAndQuery);
        Assert.DoesNotContain("+", uri.PathAndQuery);
        Assert.Contains("calendar=Family%20%2F%20Shared", uri.PathAndQuery);
    }

    [Fact]
    public async Task List_calendar_events_omits_a_query_entirely_when_nothing_narrows_it()
    {
        var handler = new CapturingHandler { ResponseBody = new { items = Array.Empty<object>() } };

        await Make(handler).ListCalendarEventsAsync();

        Assert.Equal("http://192.168.1.50:17832/v1/calendar-events", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.False(handler.Request.Headers.Contains("Idempotency-Key"));
    }

    // The five calendar failures each imply a different fix, and Erda relays them verbatim — so no
    // two of them may read the same, and none may read like its reminders counterpart.
    [Fact]
    public async Task The_five_calendar_failures_read_differently_from_each_other()
    {
        var messages = new Dictionary<string, string>();
        foreach (var code in new[]
                 {
                     "no_such_calendar", "ambiguous_calendar", "calendar_read_only",
                     "calendar_unavailable", "calendar_not_configured",
                 })
        {
            var result = await Make(new CapturingHandler
            {
                Status = HttpStatusCode.BadRequest,
                ResponseBody = new { error = code, requestId = "r1" },
            }).CreateCalendarEventAsync("x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

            Assert.False(result.Success);
            messages[code] = result.Error!;
        }

        Assert.Equal(5, messages.Values.Distinct().Count());
        Assert.Contains("no calendar with that name", messages["no_such_calendar"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rename one", messages["ambiguous_calendar"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only", messages["calendar_read_only"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Calendars permission", messages["calendar_unavailable"]);
        Assert.Contains("ErdaBridge app", messages["calendar_not_configured"]);
    }

    // Both are 503s and both stop a create, but one is a macOS permission and the other is a choice
    // nobody has made in the ErdaBridge app — and neither is "the Mac is unreachable", which is what
    // a transport failure says. Three failures, three errands.
    [Fact]
    public async Task An_unconfigured_write_calendar_reads_as_neither_a_permission_nor_an_unreachable_mac()
    {
        var notConfigured = await Make(new CapturingHandler
        {
            Status = HttpStatusCode.ServiceUnavailable,
            ResponseBody = new { error = "calendar_not_configured", requestId = "r1" },
        }).CreateCalendarEventAsync("x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        var unavailable = await Make(new CapturingHandler
        {
            Status = HttpStatusCode.ServiceUnavailable,
            ResponseBody = new { error = "calendar_unavailable", requestId = "r1" },
        }).CreateCalendarEventAsync("x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        var unreachable = await Make(new CapturingHandler
        {
            ThrowOnSend = new HttpRequestException("connection refused"),
        }).CreateCalendarEventAsync("x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        Assert.False(notConfigured.Success);
        // It names the thing to open and the thing to do there.
        Assert.Contains("ErdaBridge app", notConfigured.Error);
        Assert.Contains("choose which calendar", notConfigured.Error);
        // And says neither of the two things it is not.
        Assert.DoesNotContain("System Settings", notConfigured.Error);
        Assert.DoesNotContain("Couldn't reach", notConfigured.Error);

        Assert.Equal(3, new[] { notConfigured.Error, unavailable.Error, unreachable.Error }.Distinct().Count());
    }

    // "Grant Reminders access" and "Grant Calendar access" are two different rows in System Settings,
    // so the two 503s must not be interchangeable.
    [Fact]
    public async Task Calendar_unavailable_names_a_different_permission_from_reminders_unavailable()
    {
        var calendar = await Make(new CapturingHandler
        {
            Status = HttpStatusCode.ServiceUnavailable,
            ResponseBody = new { error = "calendar_unavailable", requestId = "r1" },
        }).CreateCalendarEventAsync("x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        var reminders = await Make(new CapturingHandler
        {
            Status = HttpStatusCode.ServiceUnavailable,
            ResponseBody = new { error = "reminders_unavailable", requestId = "r1" },
        }).CreateReminderAsync("Groceries", "x");

        Assert.NotEqual(calendar.Error, reminders.Error);
        Assert.Contains("Calendars permission", calendar.Error);
        Assert.DoesNotContain("Reminders permission", calendar.Error);
        Assert.Contains("Reminders permission", reminders.Error);
    }

    // A missing calendar and a missing list are different problems on different Macs' surfaces; the
    // wording must send Phil to the right app.
    [Fact]
    public async Task No_such_calendar_and_no_such_list_do_not_read_the_same()
    {
        var calendar = await Make(new CapturingHandler
        {
            Status = HttpStatusCode.NotFound,
            ResponseBody = new { error = "no_such_calendar", requestId = "r1" },
        }).CreateCalendarEventAsync("x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        var list = await Make(new CapturingHandler
        {
            Status = HttpStatusCode.NotFound,
            ResponseBody = new { error = "no_such_list", requestId = "r1" },
        }).CreateReminderAsync("Nope", "x");

        Assert.NotEqual(calendar.Error, list.Error);
        Assert.Contains("Calendar.app", calendar.Error);
        Assert.Contains("Reminders.app", list.Error);
    }

    [Fact]
    public async Task Calendar_transport_failure_yields_a_result_instead_of_throwing()
    {
        var handler = new CapturingHandler { ThrowOnSend = new HttpRequestException("connection refused") };

        var result = await Make(handler).CreateCalendarEventAsync(
            "x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

        Assert.False(result.Success);
        Assert.Contains("Couldn't reach", result.Error);
    }

    [Fact]
    public async Task Unconfigured_base_url_fails_a_calendar_call_without_making_one()
    {
        var handler = new CapturingHandler();
        var client = new AppleBridgeClient(
            new HttpClient(handler),
            Options.Create(new AppleBridgeOptions { BaseUrl = "", ApiKey = "x" }),
            NullLogger<AppleBridgeClient>.Instance);

        var created = await client.CreateCalendarEventAsync(
            "x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        var listed = await client.ListCalendarEventsAsync();

        Assert.False(created.Success);
        Assert.False(listed.Success);
        Assert.Null(handler.Request);
    }
}
