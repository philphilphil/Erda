using Microsoft.Extensions.AI;

namespace Erda.Core.Abstractions;

/// <summary>The result of an agent turn: the reply text plus telemetry for logging.</summary>
public sealed record AgentReply(
    string Text,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    IReadOnlyList<string> ToolsUsed)
{
    /// <summary>
    /// True when the turn is not an answer at all but an upstream model failure: empty text with no
    /// token usage AND no tool calls. That combination only happens when the streamed Responses
    /// backend fails (e.g. <c>response.failed</c>/overloaded), which the aggregation surfaces as empty
    /// text with null usage — a model that genuinely chose to say nothing still reports usage.
    /// </summary>
    public bool IsUpstreamFailure => string.IsNullOrWhiteSpace(Text) && TotalTokens is null && ToolsUsed.Count == 0;
}

/// <summary>Runs a set of chat messages through an agent and returns the reply + telemetry.</summary>
public interface IAgentResponder
{
    Task<AgentReply> RespondAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the messages in a fresh, throwaway session — used for background work (e.g. scheduled
    /// prompts) so it neither reads nor pollutes the live WhatsApp conversation.
    /// </summary>
    Task<AgentReply> RunOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Discards the conversation so the next message starts fresh.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
