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
        Middleware(IActivityRecorder recorder, ILogger logger) =>
        (agent, context, next, cancellationToken) =>
        {
            var name = context?.Function?.Name ?? "(tool)";
            recorder.Record("tool_call", name);
            // Also surface every tool call on the console logger so it's visible in `make dev`. The
            // activity feed (panel SSE) and OTel spans (Seq) otherwise carry this but neither shows in
            // the dev CLI. Tool NAME only — never argument values (which can hold note/vault content).
            logger.LogInformation("tool_call → {Tool}", name);
            return next(context!, cancellationToken);
        };
}
