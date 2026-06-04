using Erda.Core.Services.OnePassword;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// Function-invocation middleware for the <b>browser sub-agent only</b>. When the model emits a tool
/// call whose string argument is a 1Password reference (<c>op://…</c>), this resolves it to the real
/// value <i>after</i> the model emitted it, forwards the value to the actual tool (the MCP
/// <c>browser_type</c>), then restores the reference in a <c>finally</c>.
///
/// Ordering matters: this middleware is added <b>after</b> <c>UseOpenTelemetry</c> in
/// <see cref="Erda.Agents.BrowserAgent"/>, so OpenTelemetry is the outer layer and records the
/// argument <i>before</i> the swap (and, if it serializes lazily, <i>after</i> the restore). Either
/// way the recorded/telemetry copy only ever holds the <c>op://…</c> reference — never the resolved
/// secret. This runs regardless of the message-content capture flag.
///
/// Only exact top-level reference strings (a value that starts with <c>op://</c>) are resolved; the
/// browsing prompt instructs the agent to type the bare reference as the field value.
/// </summary>
public static class SecretInjection
{
    public static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>>
        Middleware(IOpSecretResolver resolver) =>
        async (agent, context, next, cancellationToken) =>
        {
            var args = context?.Arguments;
            if (args is null)
                return await next(context!, cancellationToken);

            // Resolve every op:// string arg, remembering the originals to restore afterward.
            List<KeyValuePair<string, object?>>? originals = null;
            foreach (var kv in args.ToList())
            {
                if (kv.Value is string s && s.StartsWith("op://", StringComparison.Ordinal))
                {
                    (originals ??= []).Add(kv);
                    args[kv.Key] = await resolver.ResolveAsync(s, cancellationToken);
                }
            }

            try
            {
                return await next(context!, cancellationToken);
            }
            finally
            {
                if (originals is not null)
                    foreach (var kv in originals) args[kv.Key] = kv.Value;
            }
        };
}
