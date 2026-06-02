namespace Erda.Agents.WebChat;

/// <summary>
/// Seam for the web-chat channel: streams the agent's reply token-by-token and provides a way to
/// reset the conversation. Implemented by <see cref="WebChatService"/> (a separate session from
/// the WhatsApp channel's <c>ErdaAgentResponder</c>).
/// </summary>
public interface IWebChat
{
    /// <summary>Send <paramref name="text"/> to the agent and stream back reply deltas.</summary>
    IAsyncEnumerable<string> StreamReplyAsync(string text, CancellationToken ct);

    /// <summary>Drop the current session so the next turn starts with a clean slate.</summary>
    void Reset();
}
