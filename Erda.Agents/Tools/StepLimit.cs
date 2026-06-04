using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// Function-invocation middleware that bounds a browser-sub-agent run to <c>MaxSteps</c> model
/// round-trips. After the call at the final allowed iteration completes, it sets
/// <see cref="FunctionInvocationContext.Terminate"/> so the function-invoking loop stops rather than
/// letting a confused or looping agent keep driving the browser unbounded (cost + a stuck browse_web).
/// Modeled on <see cref="Erda.Agents.ToolCallActivity"/>. A non-positive limit disables the bound.
/// </summary>
public static class StepLimit
{
    public static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>>
        Middleware(int maxSteps) =>
        async (agent, context, next, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            // Iteration is the (0-based) model round-trip count; stop once we've used up MaxSteps of them.
            if (maxSteps > 0 && context.Iteration + 1 >= maxSteps)
                context.Terminate = true;
            return result;
        };
}
