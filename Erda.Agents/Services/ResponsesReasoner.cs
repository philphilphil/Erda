using System.ClientModel;
using System.Diagnostics;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace Erda.Agents.Services;

/// <summary>
/// <see cref="IReasoner"/> backed by the local OpenAI-compatible endpoint via the OpenAI Responses
/// API. This is the in-process replacement for the old <c>codex</c> CLI: Erda's strong model
/// (<see cref="ErdaOptions.ChatModel"/>) reached over plain HTTP, with the hosted <c>web_search</c>
/// tool for grounding — exactly the capabilities Codex provided. It lives in the MAF layer
/// (not Erda.Core) because it builds <see cref="AIAgent"/>s; Core only declares the seam.
///
/// We use the <b>streamed</b> Responses surface deliberately: the proxy's non-streamed
/// <c>/responses</c> returns an empty <c>output</c>, so each call streams updates and aggregates them
/// to the final text via <see cref="AgentResponseExtensions.ToAgentResponseAsync"/>.
///
/// Failures are NOT swallowed here — they propagate. The consumers that need resilience already wrap
/// the call (the error-watch <c>CodexErrorAnalyzer</c> degrades to a short note; the reminder
/// scheduler guards its run), so a single bad call never wedges a background loop.
/// </summary>
public sealed class ResponsesReasoner : IReasoner
{
    private readonly ErdaOptions _options;
    private readonly ILogger<ResponsesReasoner> _logger;
#pragma warning disable OPENAI001 // Responses surface is [Experimental].
    private readonly ResponsesClient _responses;
#pragma warning restore OPENAI001

    public ResponsesReasoner(IOptions<ErdaOptions> options, ILogger<ResponsesReasoner> logger)
    {
        _options = options.Value;
        _logger = logger;

        // The endpoint requires no real auth (loopback proxy), but the SDK still needs a non-empty
        // credential string — blank ChatApiKey falls back to the dummy "local".
        var key = string.IsNullOrWhiteSpace(_options.ChatApiKey) ? "local" : _options.ChatApiKey;
#pragma warning disable OPENAI001 // Responses surface is [Experimental].
        _responses = new ResponsesClient(
            new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = new Uri(_options.ChatBaseUrl) });
#pragma warning restore OPENAI001
    }

    /// <inheritdoc />
    public Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken ct = default)
        => ReasonAsync($"{developerInstruction}\n\nTranscript:\n{transcript}", webSearch: true, ct, logLabel: "voice-memo processing");

    /// <inheritdoc />
    public async Task<string> ReasonAsync(
        string prompt, bool webSearch = false, CancellationToken ct = default,
        string? logLabel = null, string? reasoningEffort = null)
    {
        var effort = NormalizeReasoningEffort(reasoningEffort, _options.ChatReasoningEffort);

        // web_search is a hosted tool the model invokes itself; only attach it when asked.
        var tools = new List<AITool>();
        if (webSearch) tools.Add(new HostedWebSearchTool());

        var task = string.IsNullOrWhiteSpace(logLabel) ? "(reasoning)" : logLabel!;
        _logger.LogInformation(
            "Reasoner call: model={Model} effort={Effort} webSearch={Web} promptChars={Chars} | task={Task}",
            _options.ChatModel, effort, webSearch, prompt.Length, task);

        var sw = Stopwatch.StartNew();

        // A fresh one-shot agent per call: tools (and thus the web_search capability) vary by call, and
        // the agent is cheap to build over the shared client. Reasoning effort rides on the underlying
        // Responses request via the raw-representation hook (MEAI has no first-class ChatOptions field
        // for it). No session — each call is a stateless oracle.
#pragma warning disable OPENAI001 // Responses surface is [Experimental].
        var agent = _responses.AsAIAgent(model: _options.ChatModel, tools: tools);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            RawRepresentationFactory = _ => new CreateResponseOptions
            {
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort),
                },
            },
        });

        // The proxy's non-streamed /responses returns empty output, so we MUST stream and aggregate.
        var response = await agent.RunStreamingAsync(prompt, null, runOptions, ct).ToAgentResponseAsync(ct);
#pragma warning restore OPENAI001

        var result = (response.Text ?? string.Empty).Trim();
        if (result.Length == 0)
            throw new InvalidOperationException("Reasoner produced no output.");

        _logger.LogInformation("Reasoner completed in {ElapsedMs}ms, returned {Chars} chars.", sw.ElapsedMilliseconds, result.Length);
        return result;
    }

    /// <summary>The reasoning-effort levels the model accepts.</summary>
    private static readonly HashSet<string> ValidEfforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "minimal", "low", "medium", "high",
    };

    /// <summary>Normalize a requested reasoning effort to a known level, or fall back to the default.</summary>
    public static string NormalizeReasoningEffort(string? requested, string fallback)
    {
        var trimmed = requested?.Trim();
        return !string.IsNullOrEmpty(trimmed) && ValidEfforts.Contains(trimmed)
            ? trimmed.ToLowerInvariant()
            : fallback;
    }
}
