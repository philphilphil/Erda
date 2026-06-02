using System.Text;
using Erda.Agents.WebChat;
using Erda.Server.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for <see cref="ChatEndpoints"/>: SSE framing, error events, and CSRF protection.
/// The real <see cref="ChatEndpoints.StreamChatAsync"/> is called directly (it's <c>internal</c>)
/// against a fake <see cref="IWebChat"/> so the tests exercise the actual production code.
/// </summary>
public class ChatEndpointsTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Reads the full SSE response body and returns each <c>data: …</c> payload (the text after
    /// "data: ", with trailing whitespace stripped).
    /// </summary>
    private static async Task<List<string>> ReadSsePayloads(HttpContext http)
    {
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body, Encoding.UTF8, leaveOpen: true);
        var raw = await reader.ReadToEndAsync();

        var payloads = new List<string>();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("data: ", StringComparison.Ordinal))
                payloads.Add(trimmed["data: ".Length..]);
        }
        return payloads;
    }

    private static (HttpContext http, MemoryStream body) MakeHttpContext(string method = "POST")
    {
        var body = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Response.Body = body;
        return (http, body);
    }

    // ---------------------------------------------------------------------------
    // CSRF guard (exercised via the filter directly — routing-independent)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_chat_without_csrf_header_returns_403()
    {
        var (result, nextCalled) = await InvokeFilter("POST", header: null);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Post_chat_reset_without_csrf_header_returns_403()
    {
        var (result, nextCalled) = await InvokeFilter("POST", header: "wrong-value");
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Post_chat_with_csrf_header_passes_through()
    {
        var (_, nextCalled) = await InvokeFilter("POST", header: "erda-panel");
        Assert.True(nextCalled);
    }

    private static async Task<(object? result, bool nextCalled)> InvokeFilter(string method, string? header)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (header is not null)
            http.Request.Headers["X-Requested-With"] = header;

        var ctx = EndpointFilterInvocationContext.Create(http);
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(new object());
        };

        var result = await new CsrfEndpointFilter().InvokeAsync(ctx, next);
        return (result, nextCalled);
    }

    // ---------------------------------------------------------------------------
    // SSE framing: calls the real ChatEndpoints.StreamChatAsync (internal)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Successful_stream_emits_delta_events_then_done()
    {
        var fakeChat = new FakeWebChat(["Hello", ", ", "world"]);
        var (http, _) = MakeHttpContext();

        await ChatEndpoints.StreamChatAsync(new ChatEndpoints.ChatRequest("hi"), fakeChat, http);

        var payloads = await ReadSsePayloads(http);

        Assert.Equal(4, payloads.Count);
        Assert.Equal("{\"delta\":\"Hello\"}", payloads[0]);
        Assert.Equal("{\"delta\":\", \"}", payloads[1]);
        Assert.Equal("{\"delta\":\"world\"}", payloads[2]);
        Assert.Equal("{\"done\":true,\"sessionId\":\"sess-test\"}", payloads[3]);
    }

    [Fact]
    public async Task Delta_with_special_json_characters_is_properly_escaped()
    {
        const string original = "Say \"hello\" and \\done";
        var fakeChat = new FakeWebChat([original]);
        var (http, _) = MakeHttpContext();

        await ChatEndpoints.StreamChatAsync(new ChatEndpoints.ChatRequest("hi"), fakeChat, http);

        var payloads = await ReadSsePayloads(http);

        // Two payloads: one delta + one done. Deserialize to confirm round-trip fidelity.
        Assert.Equal(2, payloads.Count);
        var doc = System.Text.Json.JsonDocument.Parse(payloads[0]);
        Assert.Equal(original, doc.RootElement.GetProperty("delta").GetString());
    }

    [Fact]
    public async Task Error_from_service_emits_error_event()
    {
        var fakeChat = new FakeWebChat(new InvalidOperationException("boom"));
        var (http, _) = MakeHttpContext();

        await ChatEndpoints.StreamChatAsync(new ChatEndpoints.ChatRequest("hi"), fakeChat, http);

        var payloads = await ReadSsePayloads(http);
        Assert.Single(payloads);
        Assert.Contains("\"error\"", payloads[0]);
        Assert.Contains("boom", payloads[0]);
    }

    [Fact]
    public async Task Cancellation_does_not_emit_error_event()
    {
        using var cts = new CancellationTokenSource();
        var fakeChat = new FakeWebChat(["a", "b"]);
        var (http, _) = MakeHttpContext();
        http.RequestAborted = cts.Token;
        cts.Cancel();

        await ChatEndpoints.StreamChatAsync(new ChatEndpoints.ChatRequest("hi"), fakeChat, http);

        var payloads = await ReadSsePayloads(http);
        Assert.DoesNotContain(payloads, p => p.Contains("\"error\""));
    }
}

// ---------------------------------------------------------------------------
// Fake IWebChat
// ---------------------------------------------------------------------------

/// <summary>Fake <see cref="IWebChat"/> that yields canned deltas or throws a given exception.</summary>
public sealed class FakeWebChat : IWebChat
{
    private readonly string[]? _deltas;
    private readonly Exception? _exception;

    public FakeWebChat(string[] deltas) => _deltas = deltas;
    public FakeWebChat(Exception exception) => _exception = exception;

    public int Resets { get; private set; }

    public string? SessionId { get; set; } = "sess-test";

    public async IAsyncEnumerable<string> StreamReplyAsync(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_exception is not null)
            throw _exception;

        foreach (var delta in _deltas ?? [])
        {
            ct.ThrowIfCancellationRequested();
            yield return delta;
            await Task.Yield();
        }
    }

    public void Reset() => Resets++;
}
