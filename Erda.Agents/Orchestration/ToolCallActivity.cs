using Erda.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents;

/// <summary>
/// Function-invocation middleware that records one <c>tool_call</c> activity entry each time the
/// agent invokes a tool. Added once to the agent builder in <see cref="ErdaAgent"/>, so it covers
/// every channel (WhatsApp, web chat, scheduled prompts) uniformly.
///
/// Only the tool name is recorded — never argument values — because the activity feed is shown in
/// the LAN control panel and tool arguments can carry note contents, Codex context, or vault paths.
/// (OpenTelemetry traces, gated by the sensitive-data flag, remain the place for full arguments.)
/// </summary>
public static class ToolCallActivity
{
    /// <summary>
    /// Builds the function-invocation middleware delegate for the agent builder's
    /// <c>Use(...)</c> extension. Records the call (best-effort — <see cref="IActivityRecorder"/>
    /// never throws) before letting it proceed.
    /// </summary>
    public static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>>
        Middleware(IActivityRecorder recorder) =>
        (agent, context, next, cancellationToken) =>
        {
            recorder.Record("tool_call", context?.Function?.Name ?? "(tool)");
            return next(context!, cancellationToken);
        };
}
