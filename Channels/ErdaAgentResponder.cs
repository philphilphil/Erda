using Erda.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection; // FromKeyedServices

namespace Erda.Channels;

/// <summary>The result of an agent turn: the reply text plus telemetry for logging.</summary>
public sealed record AgentReply(
    string Text,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    IReadOnlyList<string> ToolsUsed);

/// <summary>Runs a set of chat messages through an agent and returns the reply + telemetry.</summary>
public interface IAgentResponder
{
    Task<AgentReply> RespondAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);
}

/// <summary>
/// Drives the registered <c>erda</c> agent (the same keyed instance the OpenAI endpoints use) with
/// a single long-lived <see cref="AgentSession"/>, so the WhatsApp conversation keeps context
/// across messages. The session is created lazily (the factory is async) and runs are serialized
/// by a semaphore because an <see cref="AgentSession"/> is not safe for concurrent use (v1 has a
/// single owner, so this is also the natural ordering).
/// </summary>
public sealed class ErdaAgentResponder(
    [FromKeyedServices(ErdaAgent.Name)] AIAgent agent) : IAgentResponder
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentSession? _session;

    public async Task<AgentReply> RespondAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session ??= await agent.CreateSessionAsync(cancellationToken);
            var response = await agent.RunAsync(messages, _session, cancellationToken: cancellationToken);

            // Tools the agent actually called this turn (distinct, in call order).
            var tools = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();
            var usage = response.Usage;

            return new AgentReply(
                response.Text ?? "",
                usage?.InputTokenCount,
                usage?.OutputTokenCount,
                usage?.TotalTokenCount,
                tools);
        }
        finally
        {
            _gate.Release();
        }
    }
}
