using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// An <see cref="IChatReducer"/> for the browser sub-agent. Playwright MCP <c>browser_snapshot</c>
/// results are huge (the full accessibility tree) and accumulate across steps, eventually blowing the
/// model's context window. This keeps the system message, all reasoning, and small tool results, but
/// replaces every large tool-result EXCEPT the most recent <see cref="_keep"/> with a short placeholder
/// (preserving its tool-call id so the message sequence stays valid). The agent only needs the current
/// page, not every historical snapshot.
/// </summary>
public sealed class BrowserSnapshotReducer(int keepLargeToolResults = 1, int largeThresholdChars = 8000) : IChatReducer
{
    private const string Placeholder = "[earlier browser snapshot omitted to save context]";
    private readonly int _keep = Math.Max(0, keepLargeToolResults);
    private readonly int _threshold = largeThresholdChars;

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        var largeIdx = new List<int>();
        for (var i = 0; i < list.Count; i++)
            if (IsLargeToolResult(list[i]))
                largeIdx.Add(i);

        if (largeIdx.Count <= _keep)
            return Task.FromResult<IEnumerable<ChatMessage>>(list);

        var keepFrom = largeIdx.Count - _keep;                       // keep the last _keep of them
        var trim = new HashSet<int>(largeIdx.Take(keepFrom));        // earlier ones to trim

        var reduced = new List<ChatMessage>(list.Count);
        for (var i = 0; i < list.Count; i++)
            reduced.Add(trim.Contains(i) ? Trimmed(list[i]) : list[i]);

        return Task.FromResult<IEnumerable<ChatMessage>>(reduced);
    }

    private bool IsLargeToolResult(ChatMessage m)
    {
        if (m.Role != ChatRole.Tool) return false;
        // Playwright MCP tool results are a single string FunctionResultContent; measure that.
        var frc = m.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        var len = (frc?.Result?.ToString() ?? m.Text)?.Length ?? 0;
        return len > _threshold;
    }

    private static ChatMessage Trimmed(ChatMessage m)
    {
        var callId = m.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId ?? "";
        return new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent(callId, Placeholder) });
    }
}
