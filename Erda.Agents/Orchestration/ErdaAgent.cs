using System.ClientModel;
using OpenAI.Responses;
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
/// Builds the Erda chat agent (the orchestrator): gpt-5.5 on the Responses API, reached via the
/// OpenAI SDK pointed at a local OpenAI-compatible endpoint. Tools = the Obsidian vault tools, the
/// process_voice_memo tool (the voice-memo MAF workflow exposed via AsAIFunction, agent-as-tool),
/// notify + reminder tools, the optional browser tool, and a native HostedWebSearchTool so Erda
/// browses the live web itself (the capability Codex used to provide).
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

        // Base URL + model are validated at startup, so they are guaranteed present here. Erda runs on
        // the Responses API against a local OpenAI-compatible endpoint (Erda__ChatBaseUrl / Erda__ChatModel)
        // so it can web-search natively via HostedWebSearchTool — the capability Codex used to provide.
        // The loopback proxy needs no real credential, so the key defaults to "local" (the SDK still
        // requires a non-empty string).
#pragma warning disable OPENAI001 // Responses surface is [Experimental]
        OpenAI.Responses.ResponsesClient responses = new OpenAI.Responses.ResponsesClient(
            credential: new ApiKeyCredential(string.IsNullOrWhiteSpace(options.ChatApiKey) ? "local" : options.ChatApiKey),
            options: new ResponsesClientOptions { Endpoint = new Uri(options.ChatBaseUrl) });
#pragma warning restore OPENAI001

        var tools = new List<AITool>();
        tools.AddRange(services.GetRequiredService<ObsidianTools>().AsTools());
        tools.Add(VoiceMemoWorkflow.CreateTool(services));
        tools.AddRange(services.GetRequiredService<NotifyTools>().AsTools());
        tools.AddRange(services.GetRequiredService<ReminderTools>().AsTools());
        tools.Add(services.GetRequiredService<VaultEditorTool>().AsTool());
        tools.Add(new HostedWebSearchTool());

        // card_price is pure HTTP (Scryfall + a prebuilt Cardmarket link) — always available.
        tools.AddRange(services.GetRequiredService<CardPriceTool>().AsTools());

        // Apple Reminders and Apple Calendar (macOS ErdaBridge), only when configured — same
        // null-when-disabled posture as BrowserAgent.TryCreateTool, so a Phil without the bridge
        // running never sees these tools. One switch for both: they are the same app, the same token
        // and the same LAN address. (The two macOS *permissions* are separate, but that is the
        // bridge's problem — an ungranted one surfaces as a readable 503 through the tool.)
        var appleBridge = services.GetRequiredService<IOptions<AppleBridgeOptions>>().Value;
        if (appleBridge.Enabled)
        {
            tools.AddRange(services.GetRequiredService<AppleReminderTools>().AsTools());
            tools.AddRange(services.GetRequiredService<AppleCalendarTools>().AsTools());
        }

        var browseTool = BrowserAgent.TryCreateTool(services);
        if (browseTool is not null) tools.Add(browseTool);

        // The active system prompt lives in the SQLite DB (authored in the control panel). There is
        // no code-baked default: a fresh DB has no system prompt until one is saved. Read once at
        // agent-build time; a panel edit applies on restart.
        var instructions = services.GetRequiredService<IPromptStore>().GetActiveContent(PromptKind.System);

        // The agent's name MUST equal the registration key (see Program.cs AddAIAgent). We build via
        // ChatClientAgentOptions (not the simple overload) so we can set the default reasoning effort
        // (Erda__ChatReasoningEffort) for every run — WhatsApp and web chat. The value is [Required] and
        // validated against ValidReasoningEfforts at startup, so there is no in-code default here; we
        // only normalize for the wire. Effort has no first-class MEAI ChatOptions field, so it rides on
        // the raw Responses request via RawRepresentationFactory.
        var effort = ResponsesReasoner.NormalizeReasoningEffort(null, options.ChatReasoningEffort);
#pragma warning disable OPENAI001 // ResponsesClient.AsAIAgent + Responses surface are [Experimental]
        var agent = responses.AsAIAgent(new ChatClientAgentOptions
        {
            Name = Name,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools,
                RawRepresentationFactory = _ => new CreateResponseOptions
                {
                    ReasoningOptions = new ResponseReasoningOptions
                    {
                        ReasoningEffortLevel = new ResponseReasoningEffortLevel(effort),
                    },
                },
            },
        }, options.ChatModel);
#pragma warning restore OPENAI001

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
