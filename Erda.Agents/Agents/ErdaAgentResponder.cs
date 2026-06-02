using Erda.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents;

/// <summary>
/// Drives the registered <c>erda</c> agent (the same keyed instance the OpenAI endpoints use) with
/// a single long-lived <see cref="AgentSession"/>, so the WhatsApp conversation keeps context
/// across messages. The session is created lazily (the factory is async) and runs are serialized
/// by a semaphore because an <see cref="AgentSession"/> is not safe for concurrent use (v1 has a
/// single owner, so this is also the natural ordering).
/// </summary>
public sealed class ErdaAgentResponder(
    [FromKeyedServices(ErdaAgent.Name)] AIAgent agent,
    IActivityRecorder recorder) : IAgentResponder
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
            var reply = ToReply(response);
            recorder.Record("agent_run", Summarize(reply), new { reply.InputTokens, reply.OutputTokens, reply.ToolsUsed });
            return reply;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A short, single-line description of a reply for the activity feed.</summary>
    private static string Summarize(AgentReply reply)
    {
        var text = reply.Text.ReplaceLineEndings(" ").Trim();
        if (text.Length > 100)
            text = text[..100] + "…";
        return string.IsNullOrEmpty(text) ? "(no text reply)" : text;
    }

    public async Task<AgentReply> RunOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        // A fresh session, independent of the live conversation's _session/_gate, so background
        // prompts don't block or pollute the live WhatsApp turn.
        var session = await agent.CreateSessionAsync(cancellationToken);
        var response = await agent.RunAsync(messages, session, cancellationToken: cancellationToken);
        return ToReply(response);
    }

    private static AgentReply ToReply(AgentResponse response)
    {
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

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _session = null; // next RespondAsync creates a fresh session
        }
        finally
        {
            _gate.Release();
        }
    }
}
