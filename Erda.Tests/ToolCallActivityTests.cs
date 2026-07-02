using Erda.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for <see cref="ToolCallActivity"/>: the function-invocation middleware records one
/// <c>tool_call</c> entry per call (name, plus argument values in the summary + structured detail)
/// and always chains through to the actual invocation.
/// </summary>
public class ToolCallActivityTests
{
    [Fact]
    public async Task Records_the_function_name_and_chains_next()
    {
        var recorder = new FakeActivityRecorder();
        var middleware = ToolCallActivity.Middleware(recorder, NullLogger.Instance);

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
        Assert.Equal("echo", entry.Summary);           // no args → summary is just the name
        Assert.Null(entry.Detail);                     // and no structured detail
    }

    [Fact]
    public async Task Records_argument_values_in_summary_and_detail()
    {
        var recorder = new FakeActivityRecorder();
        var middleware = ToolCallActivity.Middleware(recorder, NullLogger.Instance);

        var fn = AIFunctionFactory.Create((string path, bool overwrite) => "ok", "write_note");
        var context = new FunctionInvocationContext
        {
            Function = fn,
            Arguments = new AIFunctionArguments { ["path"] = "1 Inbox/x.md", ["overwrite"] = true },
        };

        await middleware(null!, context, (ctx, ct) => ValueTask.FromResult<object?>("done"), CancellationToken.None);

        var entry = Assert.Single(recorder.Records);
        Assert.Equal("tool_call", entry.Kind);
        Assert.StartsWith("write_note(", entry.Summary);        // args folded inline into the summary
        Assert.Contains("path=1 Inbox/x.md", entry.Summary);
        var detail = Assert.IsType<Dictionary<string, string>>(entry.Detail);
        Assert.Equal("1 Inbox/x.md", detail["path"]);           // full args in structured detail
        Assert.Equal("true", detail["overwrite"]);
    }

    [Fact]
    public async Task Console_log_line_stays_name_only_so_args_never_reach_seq()
    {
        var recorder = new FakeActivityRecorder();
        var logger = new CapturingLogger();
        var middleware = ToolCallActivity.Middleware(recorder, logger);

        var fn = AIFunctionFactory.Create((string path) => "ok", "write_note");
        var context = new FunctionInvocationContext
        {
            Function = fn,
            Arguments = new AIFunctionArguments { ["path"] = "1 Inbox/secret.md" },
        };
        await middleware(null!, context, (ctx, ct) => ValueTask.FromResult<object?>("done"), CancellationToken.None);

        // The activity feed (recorder) carries the args; the Serilog line (→ Seq) must be name-only.
        var line = Assert.Single(logger.Messages);
        Assert.Contains("write_note", line);
        Assert.DoesNotContain("1 Inbox/secret.md", line);
    }

    [Fact]
    public async Task Records_a_placeholder_when_the_function_is_unknown()
    {
        var recorder = new FakeActivityRecorder();
        var middleware = ToolCallActivity.Middleware(recorder, NullLogger.Instance);

        var context = new FunctionInvocationContext();  // no Function set

        await middleware(null!, context, (ctx, ct) => ValueTask.FromResult<object?>(null), CancellationToken.None);

        Assert.Equal("tool_call", Assert.Single(recorder.Records).Kind);
    }
}
