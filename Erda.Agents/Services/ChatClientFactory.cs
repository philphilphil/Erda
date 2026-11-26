using System.ClientModel;
using Erda.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Erda.Agents.Services;

/// <summary>
/// The single place Erda talks to its chat model. It owns the one <see cref="OpenAIClient"/>,
/// pointed at the OpenAI-compatible LiteLLM endpoint (<see cref="ErdaOptions.ChatBaseUrl"/>) with a real key
/// (<see cref="ErdaOptions.ChatApiKey"/>). Every LLM consumer goes through it: the <c>erda</c> agent, the
/// in-process reasoner and the vault-editor sub-agent.
/// <para>
/// It uses <b>Chat Completions</b>, LiteLLM's native surface, which every model it fronts supports.
/// Reasoning effort and web search map to the wire only here, through MEAI's provider-neutral
/// <see cref="ChatOptions.Reasoning"/> and <see cref="HostedWebSearchTool"/>. That keeps model-specific
/// knobs out of the callers, and it is the seam per-task model routing plugs into later.
/// </para>
/// </summary>
public sealed class ChatClientFactory
{
    /// <summary>A long ceiling: a high-effort Opus turn with tools and web search can think for minutes.
    /// The streamed callers stay well inside it. A non-streamed call would hit the SDK's 100 s default.</summary>
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);

    private readonly ErdaOptions _options;
    private readonly OpenAIClient _client;

    public ChatClientFactory(IOptions<ErdaOptions> options)
        : this(options.Value, transport: null) { }

    /// <summary>Test seam: a custom pipeline transport lets a test capture the request body on the wire.</summary>
    internal ChatClientFactory(ErdaOptions options, System.ClientModel.Primitives.PipelineTransport? transport)
    {
        _options = options;
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.ChatBaseUrl),
            NetworkTimeout = NetworkTimeout,
        };
        if (transport is not null)
            clientOptions.Transport = transport;
        _client = new OpenAIClient(new ApiKeyCredential(options.ChatApiKey), clientOptions);
    }

    /// <summary>The configured model id (<see cref="ErdaOptions.ChatModel"/>), for logging.</summary>
    public string Model => _options.ChatModel;

    /// <summary>The configured default reasoning effort, already validated at startup.</summary>
    public string DefaultEffort => _options.ChatReasoningEffort;

    /// <summary>An <see cref="IChatClient"/> for the configured model. Cheap: it wraps the shared client.</summary>
    public IChatClient CreateChatClient() => _client.GetChatClient(_options.ChatModel).AsIChatClient();

    /// <summary>
    /// Builds the <see cref="ChatOptions"/> for a call: instructions, the caller's tools, plus
    /// <see cref="HostedWebSearchTool"/> when <paramref name="webSearch"/> is on, and the reasoning effort.
    /// </summary>
    public static ChatOptions BuildOptions(string? instructions, IEnumerable<AITool>? tools, bool webSearch, string effort)
    {
        var all = tools?.ToList() ?? [];
        if (webSearch && !all.OfType<HostedWebSearchTool>().Any())
            all.Add(new HostedWebSearchTool());

        return new ChatOptions
        {
            Instructions = instructions,
            Tools = all.Count == 0 ? null : all,
            Reasoning = new ReasoningOptions { Effort = ToEffort(effort) },
        };
    }

    /// <summary>Maps a validated effort name (see <see cref="ErdaOptions.ValidReasoningEfforts"/>) to MEAI's enum.</summary>
    public static ReasoningEffort ToEffort(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        var other => throw new ArgumentOutOfRangeException(nameof(effort), other,
            $"Unknown reasoning effort; expected one of {string.Join(", ", ErdaOptions.ValidReasoningEfforts)}."),
    };
}
