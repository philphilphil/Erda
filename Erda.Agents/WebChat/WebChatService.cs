using Erda.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents.WebChat;

/// <summary>
/// Drives the <c>erda</c> agent for the web-chat channel with its own <see cref="AgentSession"/>
/// and serialization gate — completely separate from <see cref="ErdaAgentResponder"/>'s session so
/// the WhatsApp conversation and the browser chat are independent threads with no shared locking.
/// </summary>
public sealed class WebChatService(
    [FromKeyedServices(ErdaAgent.Name)] AIAgent agent,
    CurrentTimeContext timeContext,
    IActivityRecorder recorder) : IWebChat
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentSession? _session;
    private string? _sessionId;

    /// <inheritdoc />
    public string? SessionId => Volatile.Read(ref _sessionId);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamReplyAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        var accumulated = new System.Text.StringBuilder();
        var completed = false;
        try
        {
            if (_session is null)
            {
                _session = await agent.CreateSessionAsync(ct);
                // Mint a fresh id for the new session so the browser can tell, on reload, whether
                // its persisted history still belongs to a session the agent actually remembers.
                Volatile.Write(ref _sessionId, Guid.NewGuid().ToString("N"));
            }

            var turn = new List<ChatMessage>
            {
                timeContext.Message(),
                new ChatMessage(ChatRole.User, text),
            };

            await foreach (var update in agent.RunStreamingAsync(turn, _session, null, ct))
            {
                // Prefer the convenience .Text property; fall back to concatenating TextContent items.
                var delta = update.Text;
                if (string.IsNullOrEmpty(delta))
                {
                    delta = string.Concat(
                        update.Contents.OfType<TextContent>().Select(c => c.Text));
                }

                if (!string.IsNullOrEmpty(delta))
                {
                    accumulated.Append(delta);
                    yield return delta;
                }
            }

            completed = true;
        }
        finally
        {
            _gate.Release();

            // Only record on successful completion — a cancelled or faulted turn is not a real
            // agent run worth surfacing in the Activity feed (matches ErdaAgentResponder's pattern).
            if (completed)
            {
                var full = accumulated.ToString().ReplaceLineEndings(" ").Trim();
                var summary = full.Length > 100 ? full[..100] + "…" : full;
                recorder.Record("agent_run", string.IsNullOrEmpty(summary) ? "(no text reply)" : summary);
            }
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        // Intentionally non-gated: Reset() is fire-and-forget from the HTTP handler. A racing
        // StreamReplyAsync would still be running on the old (discarded) session and the next
        // call will lazily create a new one. Volatile.Write ensures the null is visible to other
        // threads without a full memory fence.
        Volatile.Write(ref _session, null);
        Volatile.Write(ref _sessionId, null);
    }
}
