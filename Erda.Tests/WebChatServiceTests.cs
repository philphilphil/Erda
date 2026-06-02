using Erda.Agents;
using Erda.Agents.WebChat;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Unit tests for <see cref="WebChatService"/>: delta streaming order, session reuse across turns,
/// session reset, and activity recording. Uses a <see cref="SessionTrackingChatClient"/> that
/// records each call so tests can verify whether the agent created a new session (empty history)
/// or reused an existing one (accumulated history from prior turns).
/// </summary>
public class WebChatServiceTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static (WebChatService service, FakeActivityRecorder recorder, SessionTrackingChatClient client)
        MakeService(params string[] deltas)
    {
        var recorder = new FakeActivityRecorder();
        var client = new SessionTrackingChatClient(deltas);
        // ChatClientAgent is the concrete MAF agent built from an IChatClient.
        var agent = new ChatClientAgent(client, instructions: "test", name: ErdaAgent.Name,
            description: null, tools: null, loggerFactory: null, services: null);
        var timeContext = new CurrentTimeContext(new FakeClock(), Options.Create(new ReminderOptions()));
        var service = new WebChatService(agent, timeContext, recorder);
        return (service, recorder, client);
    }

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Deltas_are_yielded_in_order_and_concatenate_to_full_reply()
    {
        var (svc, _, _) = MakeService("Hello", ", ", "world");

        var deltas = new List<string>();
        await foreach (var d in svc.StreamReplyAsync("hi", CancellationToken.None))
            deltas.Add(d);

        Assert.Equal(["Hello", ", ", "world"], deltas);
        Assert.Equal("Hello, world", string.Concat(deltas));
    }

    [Fact]
    public async Task One_activity_entry_is_recorded_per_turn()
    {
        var (svc, recorder, _) = MakeService("Reply text here");

        await foreach (var _ in svc.StreamReplyAsync("question", CancellationToken.None)) { }

        Assert.Single(recorder.Records);
        Assert.Equal("agent_run", recorder.Records[0].Kind);
        Assert.Contains("Reply text", recorder.Records[0].Summary);
    }

    [Fact]
    public async Task Summary_is_truncated_to_100_chars_with_ellipsis()
    {
        var longReply = new string('x', 120);
        var (svc, recorder, _) = MakeService(longReply);

        await foreach (var _ in svc.StreamReplyAsync("q", CancellationToken.None)) { }

        var summary = recorder.Records[0].Summary;
        Assert.True(summary.Length <= 102, $"Summary too long: {summary.Length}");
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public async Task Cancelled_turn_does_not_record_activity()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (svc, recorder, _) = MakeService("delta");

        try
        {
            await foreach (var _ in svc.StreamReplyAsync("q", cts.Token)) { }
        }
        catch (OperationCanceledException) { /* expected */ }

        Assert.Empty(recorder.Records);
    }

    [Fact]
    public async Task Session_is_reused_across_two_turns()
    {
        // ChatClientAgent accumulates history in the session. On the first turn the client
        // receives only the current user message (no prior assistant turns = fresh session).
        // On the second turn it receives the prior exchange too (= reused session).
        var (svc, recorder, client) = MakeService("A");

        await foreach (var _ in svc.StreamReplyAsync("turn1", CancellationToken.None)) { }
        Assert.Equal(1, client.NewSessionCalls);   // turn1 started a fresh session
        Assert.Equal(1, client.StreamingCalls);

        await foreach (var _ in svc.StreamReplyAsync("turn2", CancellationToken.None)) { }
        Assert.Equal(1, client.NewSessionCalls);   // still only ONE new-session call — reused
        Assert.Equal(2, client.StreamingCalls);    // but two total streaming calls
        Assert.Equal(2, recorder.Records.Count);   // one activity per turn
    }

    [Fact]
    public async Task Reset_starts_a_fresh_session_on_next_turn()
    {
        var (svc, _, client) = MakeService("A");

        await foreach (var _ in svc.StreamReplyAsync("before", CancellationToken.None)) { }
        Assert.Equal(1, client.NewSessionCalls);

        svc.Reset();

        await foreach (var _ in svc.StreamReplyAsync("after", CancellationToken.None)) { }
        // After Reset, a second CreateSessionAsync must have been called.
        Assert.Equal(2, client.NewSessionCalls);
    }
}

// ---------------------------------------------------------------------------
// Session-tracking fake IChatClient
// ---------------------------------------------------------------------------

/// <summary>
/// Streaming fake <see cref="IChatClient"/> that:
/// <list type="bullet">
/// <item>Yields canned text deltas.</item>
/// <item>Counts total streaming calls (<see cref="StreamingCalls"/>).</item>
/// <item>Counts "new session" calls, detected by the absence of any prior assistant message in the
///   incoming history — <see cref="ChatClientAgent"/> prepends accumulated history, so a turn on a
///   reused session always carries at least one prior assistant turn.</item>
/// </list>
/// </summary>
internal sealed class SessionTrackingChatClient(params string[] deltas) : IChatClient
{
    /// <summary>Total number of <see cref="GetStreamingResponseAsync"/> calls.</summary>
    public int StreamingCalls { get; private set; }

    /// <summary>
    /// Number of calls where no prior assistant message was present — i.e. a freshly created
    /// session (or the very first turn after <see cref="WebChatService.Reset"/>).
    /// </summary>
    public int NewSessionCalls { get; private set; }

    public ChatClientMetadata Metadata => new("fake", null, null);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Only streaming is exercised by WebChatService.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamingCalls++;

        // A new session has no prior assistant turns in the message history.
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        if (!messageList.Any(m => m.Role == ChatRole.Assistant))
            NewSessionCalls++;

        foreach (var delta in deltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(delta)]);
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
