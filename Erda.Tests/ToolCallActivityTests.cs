using Erda.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for <see cref="ToolCallActivity"/>: the function-invocation middleware records one
/// <c>tool_call</c> entry (name only) per call and always chains through to the actual invocation.
/// </summary>
public class ToolCallActivityTests
{
    [Fact]
    public async Task Records_the_function_name_and_chains_next()
    {
        var recorder = new FakeActivityRecorder();
        var middleware = ToolCallActivity.Middleware(recorder);

        var fn = AIFunctionFactory.Create(() => "ok", "echo");
        var context = new FunctionInvocationContext { Function = fn };

        var nextCalled = false;
        var result = await middleware(
            null!,
            context,
            (ctx, ct) =>
            {
                nextCalled = true;
                return ValueTask.FromResult<object?>("done");
            },
            CancellationToken.None);

        Assert.True(nextCalled);                       // always proceeds with the real invocation
        Assert.Equal("done", result);                  // and returns its result unchanged

        var entry = Assert.Single(recorder.Records);
        Assert.Equal("tool_call", entry.Kind);
        Assert.Equal("echo", entry.Summary);           // name only — no argument values
    }

    [Fact]
    public async Task Records_a_placeholder_when_the_function_is_unknown()
    {
        var recorder = new FakeActivityRecorder();
        var middleware = ToolCallActivity.Middleware(recorder);

        var context = new FunctionInvocationContext();  // no Function set

        await middleware(null!, context, (ctx, ct) => ValueTask.FromResult<object?>(null), CancellationToken.None);

        Assert.Equal("tool_call", Assert.Single(recorder.Records).Kind);
    }
}
