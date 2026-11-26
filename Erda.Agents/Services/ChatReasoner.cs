using System.Diagnostics;
using Erda.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Services;

/// <summary>
/// <see cref="IReasoner"/> backed by the chat model through <see cref="ChatClientFactory"/>: a
/// one-shot, stateless <c>prompt (+ optional web search) → final text</c> call. It lives in the MAF
/// layer (not Erda.Core) because it builds <see cref="AIAgent"/>s; Core only declares the seam.
/// <para>
/// Each call streams and aggregates via <see cref="AgentResponseExtensions.ToAgentResponseAsync"/>.
/// A high-effort generation can run for minutes, and a stream keeps the connection busy where a
/// single blocking response would sit idle against HTTP timeouts.
/// </para>
/// <para>
/// Failures are NOT swallowed here; they propagate. The consumers that need resilience already wrap
/// the call: the error-watch analyzer degrades to a short note, and the voice-memo path saves the raw
/// transcript. So one bad call never wedges a background loop.
/// </para>
/// </summary>
public sealed class ChatReasoner(ChatClientFactory factory, ILogger<ChatReasoner> logger) : IReasoner
{
    /// <inheritdoc />
    public Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken ct = default)
        => ReasonAsync($"{developerInstruction}\n\nTranscript:\n{transcript}", webSearch: true, ct, logLabel: "voice-memo processing");

    /// <inheritdoc />
    public async Task<string> ReasonAsync(string prompt, bool webSearch = false, CancellationToken ct = default, string? logLabel = null)
    {
        var effort = factory.DefaultEffort;
        var task = string.IsNullOrWhiteSpace(logLabel) ? "(reasoning)" : logLabel!;
        logger.LogInformation(
            "Reasoner call: model={Model} effort={Effort} webSearch={Web} promptChars={Chars} | task={Task}",
            factory.Model, effort, webSearch, prompt.Length, task);

        var sw = Stopwatch.StartNew();

        // A fresh one-shot agent per call (tools vary by call; the agent is cheap over the shared
        // client). No session: each call is a stateless oracle.
        AIAgent agent = factory.CreateChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "reasoner",
            ChatOptions = ChatClientFactory.BuildOptions(instructions: null, tools: null, webSearch, effort),
        });

        var response = await agent.RunStreamingAsync(prompt, null, null, ct).ToAgentResponseAsync(ct);

        var result = (response.Text ?? string.Empty).Trim();
        if (result.Length == 0)
            throw new InvalidOperationException("Reasoner produced no output.");

        logger.LogInformation("Reasoner completed in {ElapsedMs}ms, returned {Chars} chars.", sw.ElapsedMilliseconds, result.Length);
        return result;
    }
}
