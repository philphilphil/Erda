using Erda.Agents.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class StepLimitTests
{
    private static FunctionInvocationContext Ctx(int iteration) =>
        new() { Function = AIFunctionFactory.Create(() => "ok", "tool"), Iteration = iteration };

    private static async Task<(bool nextCalled, object? result, bool terminate)> Run(int maxSteps, int iteration)
    {
        var mw = StepLimit.Middleware(maxSteps);
        var ctx = Ctx(iteration);
        var nextCalled = false;
        var result = await mw(null!, ctx,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult<object?>("done"); },
            CancellationToken.None);
        return (nextCalled, result, ctx.Terminate);
    }

    [Fact]
    public async Task Lets_the_call_proceed_and_does_not_terminate_below_the_limit()
    {
        var (nextCalled, result, terminate) = await Run(maxSteps: 5, iteration: 2);
        Assert.True(nextCalled);
        Assert.Equal("done", result);
        Assert.False(terminate);
    }

    [Fact]
    public async Task Terminates_once_the_iteration_reaches_the_limit()
    {
        var (nextCalled, _, terminate) = await Run(maxSteps: 5, iteration: 4);   // 4 + 1 == 5
        Assert.True(nextCalled);    // the current call still completes
        Assert.True(terminate);     // …but the loop is told to stop
    }

    [Fact]
    public async Task Is_disabled_when_maxSteps_is_zero_or_negative()
    {
        Assert.False((await Run(maxSteps: 0, iteration: 1000)).terminate);
        Assert.False((await Run(maxSteps: -1, iteration: 1000)).terminate);
    }
}
