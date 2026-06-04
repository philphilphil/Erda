using Erda.Agents;
using Erda.Agents.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class BrowserAgentGateTests
{
    private sealed class FakeMcp(bool enabled, int toolCount) : IBrowserMcp
    {
        public bool Enabled => enabled;
        public IReadOnlyList<AITool> Tools { get; } =
            [.. Enumerable.Range(0, toolCount).Select(i => AIFunctionFactory.Create(() => "x", $"browser_tool_{i}"))];
        public McpServerStatus Status => new("playwright", "stdio", enabled, []);
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void ShouldExpose_true_when_enabled_and_tools_present()
        => Assert.True(BrowserAgent.ShouldExpose(new FakeMcp(enabled: true, toolCount: 3)));

    [Fact]
    public void ShouldExpose_false_when_disabled()
        => Assert.False(BrowserAgent.ShouldExpose(new FakeMcp(enabled: false, toolCount: 3)));

    [Fact]
    public void ShouldExpose_false_when_enabled_but_no_tools_connected()
        => Assert.False(BrowserAgent.ShouldExpose(new FakeMcp(enabled: true, toolCount: 0)));
}
