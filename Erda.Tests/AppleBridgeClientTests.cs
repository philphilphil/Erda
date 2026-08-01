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

    private static readonly object StatusOk = new { availability = "ok", lists = new[] { "Groceries" } };

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
            ResponseBody = new { availability = "ok", lists = new[] { "Groceries", "Work" } },
        };

        var result = await Make(handler).GetStatusAsync();

        Assert.True(result.Success);
        Assert.Equal("ok", result.Value!.Availability);
        Assert.Equal(["Groceries", "Work"], result.Value!.Lists);
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
}
