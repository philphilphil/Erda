namespace Erda.Agents.WebChat;

/// <summary>
/// Seam for the web-chat channel: streams the agent's reply token-by-token and provides a way to
/// reset the conversation. Implemented by <see cref="WebChatService"/> (a separate session from
/// the WhatsApp channel's <c>ErdaAgentResponder</c>).
/// </summary>
public interface IWebChat
{
    /// <summary>
    /// Identifier of the current conversation session, or <c>null</c> when no session exists yet
    /// (a fresh start or just after <see cref="Reset"/>). A new id is minted whenever a session is
    /// created, so the browser can detect when the agent's in-memory history has been lost (e.g. a
    /// restart) and reconcile its locally persisted chat history.
    /// </summary>
    string? SessionId { get; }

    /// <summary>Send <paramref name="text"/> to the agent and stream back reply deltas.</summary>
    IAsyncEnumerable<string> StreamReplyAsync(string text, CancellationToken ct);

    /// <summary>Drop the current session so the next turn starts with a clean slate.</summary>
    void Reset();
}
