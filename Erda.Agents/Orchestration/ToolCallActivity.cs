using System.Text.Json;
using Erda.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents;

/// <summary>
/// Function-invocation middleware that records one <c>tool_call</c> activity entry each time the
/// agent invokes a tool. Added once to the agent builder in <see cref="ErdaAgent"/>, so it covers
/// every channel (WhatsApp, web chat, scheduled prompts) uniformly.
///
/// The entry now carries the call's <b>arguments</b>: a compact inline rendering in the summary
/// (e.g. <c>write_note(path=1 Inbox/x.md, …)</c>) plus the full name→value map in the entry's
/// structured detail. This is deliberately scoped to the control-panel activity feed only
/// (SQLite + panel SSE). The console/Serilog line — which flows to Seq — stays <b>name-only</b>,
/// so argument values (which can hold note or vault content) never leave the box via Seq. OpenTelemetry
/// traces remain the (sensitive-data-gated) place for full arguments on the tracing side.
/// </summary>
public static class ToolCallActivity
{
    // Cap a single argument's rendered length so a note/vault payload can't bloat a feed row.
    private const int MaxArgChars = 4000;

    // Cap the inline args string folded into the summary (the full set still rides in the detail).
    private const int MaxSummaryArgsChars = 300;

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
            var args = BuildArgs(context?.Arguments);

            var summary = args.Count == 0 ? name : $"{name}({InlineArgs(args)})";
            recorder.Record("tool_call", summary, args.Count == 0 ? null : args);

            // Console (→ Serilog → Seq) stays NAME-ONLY on purpose: argument values can carry note
            // or vault content, and the panel activity feed (DB + SSE) is the place for the params.
            logger.LogInformation("tool_call → {Tool}", name);
            return next(context!, cancellationToken);
        };

    /// <summary>Stringify the call's arguments into a name→value map, each value bounded in size.</summary>
    private static Dictionary<string, string> BuildArgs(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        var result = new Dictionary<string, string>();
        if (arguments is null)
            return result;
        foreach (var (key, value) in arguments)
            result[key] = Truncate(Stringify(value), MaxArgChars);
        return result;
    }

    /// <summary>Fold the args into a single compact, one-line, length-bounded string for the summary.</summary>
    private static string InlineArgs(Dictionary<string, string> args)
    {
        var joined = string.Join(", ", args.Select(kv => $"{kv.Key}={OneLine(kv.Value)}"));
        return Truncate(joined, MaxSummaryArgsChars);
    }

    /// <summary>Render an argument value as a string; complex values become JSON. Never throws.</summary>
    private static string Stringify(object? value)
    {
        try
        {
            return value switch
            {
                null => "null",
                string s => s,
                _ => JsonSerializer.Serialize(value),
            };
        }
        catch
        {
            return value?.ToString() ?? "null";
        }
    }

    private static string OneLine(string s) => s.ReplaceLineEndings(" ").Trim();

    private static string Truncate(string s, int max) => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
