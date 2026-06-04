using Erda.Agents.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class BrowserSnapshotReducerTests
{
    private static ChatMessage Sys(string t) => new(ChatRole.System, t);
    private static ChatMessage User(string t) => new(ChatRole.User, t);
    private static ChatMessage Asst(string t) => new(ChatRole.Assistant, t);
    private static ChatMessage Tool(string callId, string result) =>
        new(ChatRole.Tool, new AIContent[] { new FunctionResultContent(callId, result) });

    private static string Big(int n) => new('x', n);

    [Fact]
    public async Task Keeps_only_the_most_recent_large_tool_result_and_drops_older_ones()
    {
        var reducer = new BrowserSnapshotReducer(keepLargeToolResults: 1, largeThresholdChars: 1000);
        var messages = new List<ChatMessage>
        {
            Sys("you control a browser"),
            User("do the task"),
            Asst("navigating"),
            Tool("c1", Big(5000)),    // old snapshot — should be dropped
            Asst("thinking"),
            Tool("c2", "small ok"),   // small tool result — keep
            Asst("snapshotting"),
            Tool("c3", Big(5000)),    // latest snapshot — keep
        };

        var reduced = (await reducer.ReduceAsync(messages)).ToList();

        Assert.Equal(messages.Count, reduced.Count);                 // same count (we replace, not delete)
        // c1 was trimmed to the placeholder but keeps its CallId
        var c1 = reduced[3].Contents.OfType<FunctionResultContent>().Single();
        Assert.Equal("c1", c1.CallId);
        Assert.DoesNotContain(new string('x', 100), c1.Result?.ToString() ?? "");   // big text gone
        // c2 small tool result is untouched
        Assert.Equal("small ok", reduced[5].Contents.OfType<FunctionResultContent>().Single().Result?.ToString());
        // c3 (latest large) is preserved in full
        Assert.Contains(new string('x', 5000), reduced[7].Contents.OfType<FunctionResultContent>().Single().Result?.ToString());
        // system + reasoning preserved
        Assert.Equal(ChatRole.System, reduced[0].Role);
        Assert.Equal("do the task", reduced[1].Text);
    }

    [Fact]
    public async Task Passes_through_unchanged_when_few_large_results()
    {
        var reducer = new BrowserSnapshotReducer(keepLargeToolResults: 2, largeThresholdChars: 1000);
        var messages = new List<ChatMessage> { Sys("s"), Tool("c1", "small"), Tool("c2", new string('x', 5000)) };

        var reduced = (await reducer.ReduceAsync(messages)).ToList();

        Assert.Equal(3, reduced.Count);
        // only one large result, keepLargeToolResults=2 → nothing trimmed
        Assert.Contains(new string('x', 5000), reduced[2].Contents.OfType<FunctionResultContent>().Single().Result?.ToString());
    }
}
