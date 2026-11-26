using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Services;
using Erda.Agents.Services;
using Erda.Agents.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Erda.Agents.Workflows;

namespace Erda.Agents;

/// <summary>
/// Builds the Erda chat agent (the orchestrator) over <see cref="ChatClientFactory"/>, i.e. the
/// configured model on the LiteLLM endpoint. Tools = the Obsidian vault tools, the process_voice_memo
/// tool (the voice-memo MAF workflow exposed via AsAIFunction, agent-as-tool), notify + reminder tools,
/// the vault editor, card prices, the optional Apple bridge tools, and hosted web search.
/// </summary>
public static class ErdaAgent
{
    public const string Name = "erda";

    public static AIAgent Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<ErdaOptions>>().Value;
        var observability = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
        var recorder = services.GetRequiredService<IActivityRecorder>();
        var toolLog = services.GetRequiredService<ILoggerFactory>().CreateLogger("Erda.ToolCalls");

        var factory = services.GetRequiredService<ChatClientFactory>();

        var tools = new List<AITool>();
        tools.AddRange(services.GetRequiredService<ObsidianTools>().AsTools());
        tools.Add(VoiceMemoWorkflow.CreateTool(services));
        tools.AddRange(services.GetRequiredService<NotifyTools>().AsTools());
        tools.AddRange(services.GetRequiredService<ReminderTools>().AsTools());
        tools.Add(services.GetRequiredService<VaultEditorTool>().AsTool());

        // card_price is pure HTTP (Scryfall + a prebuilt Cardmarket link) — always available.
        tools.AddRange(services.GetRequiredService<CardPriceTool>().AsTools());

        // Apple Reminders and Apple Calendar (macOS ErdaBridge), only when configured, so a Phil
        // without the bridge running never sees these tools. One switch for both: they are the same
        // app, the same token and the same LAN address. (The two macOS *permissions* are separate,
        // but that is the bridge's problem — an ungranted one surfaces as a readable 503 through the
        // tool.)
        var appleBridge = services.GetRequiredService<IOptions<AppleBridgeOptions>>().Value;
        if (appleBridge.Enabled)
        {
            tools.AddRange(services.GetRequiredService<AppleReminderTools>().AsTools());
            tools.AddRange(services.GetRequiredService<AppleCalendarTools>().AsTools());
        }

        // The active system prompt lives in the SQLite DB (authored in the control panel). There is
        // no code-baked default: a fresh DB has no system prompt until one is saved. Read once at
        // agent-build time; a panel edit applies on restart.
        var instructions = services.GetRequiredService<IPromptStore>().GetActiveContent(PromptKind.System);

        // The agent's name MUST equal the registration key (see Program.cs AddAIAgent). Web search is
        // always on for the orchestrator; effort is the startup-validated Erda__ChatReasoningEffort.
        //
        // History: the agent keeps each session's conversation in memory and replays it every turn
        // (Chat Completions is stateless), so the long-lived WhatsApp session would grow without bound.
        // MessageCountingChatReducer keeps the newest ChatHistoryMaxMessages non-system messages and
        // drops tool-call/result messages from the replayed history. They belong to finished turns.
        var agent = factory.CreateChatClient().AsAIAgent(new ChatClientAgentOptions
        {
            Name = Name,
            ChatOptions = ChatClientFactory.BuildOptions(instructions, tools, webSearch: true, factory.DefaultEffort),
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
            {
#pragma warning disable MEAI001 // MessageCountingChatReducer is [Experimental] in MEAI 11.
                ChatReducer = new MessageCountingChatReducer(options.ChatHistoryMaxMessages!.Value),
#pragma warning restore MEAI001
            }),
        });

        // Instrument with OpenTelemetry (MAF builder): spans for the agent run, the model call
        // (token usage), and each tool/function invocation, emitted on ActivitySourceName.
        // EnableSensitiveData records prompts + tool arguments; gated by the config flag (off in
        // production), mirroring the OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT env var.
        return agent
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: ObservabilityOptions.ActivitySourceName,
                configure: telemetry => telemetry.EnableSensitiveData = observability.CaptureMessageContent)
            // Record each tool invocation to the panel activity feed (DB + SSE): tool name + argument
            // values. The Serilog/console line stays name-only, so argument values never reach Seq.
            .Use(ToolCallActivity.Middleware(recorder, toolLog))
            .Build();
    }
}
