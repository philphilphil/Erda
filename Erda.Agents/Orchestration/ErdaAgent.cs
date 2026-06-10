using System.ClientModel;
using Azure.AI.OpenAI;
using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Services;
using Erda.Agents.Tools;
using Erda.Core.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Erda.Agents.Workflows;

namespace Erda.Agents;

/// <summary>
/// Builds the Erda chat agent (the orchestrator): gpt-5-mini on Azure AI Foundry, reached via
/// the Azure OpenAI client with API-key auth. Tools = the five Obsidian vault tools plus the
/// process_voice_memo tool, which is the voice-memo MAF workflow exposed via AsAIFunction
/// (agent-as-tool). Erda routes voice memos into the workflow rather than the workflow being a
/// separate top-level agent.
/// </summary>
public static class ErdaAgent
{
    public const string Name = "erda";

    public static AIAgent Create(IServiceProvider services)
    {
        var creds = services.GetRequiredService<IOptions<CredentialsOptions>>().Value;
        var options = services.GetRequiredService<IOptions<ErdaOptions>>().Value;
        var observability = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
        var recorder = services.GetRequiredService<IActivityRecorder>();

        // Foundry endpoint + key are validated at startup, so they are guaranteed present here.
        ChatClient chatClient = new AzureOpenAIClient(
                new Uri(creds.AzureOpenAIEndpoint),
                new ApiKeyCredential(creds.AzureOpenAIApiKey))
            .GetChatClient(options.ChatDeployment);

        var tools = new List<AITool>();
        tools.AddRange(services.GetRequiredService<ObsidianTools>().AsTools());
        tools.Add(VoiceMemoWorkflow.CreateTool(services));
        tools.AddRange(services.GetRequiredService<ReasoningTools>().AsTools());
        tools.AddRange(services.GetRequiredService<NotifyTools>().AsTools());
        tools.AddRange(services.GetRequiredService<ReminderTools>().AsTools());

        var browseTool = BrowserAgent.TryCreateTool(services);
        if (browseTool is not null) tools.Add(browseTool);

        // The active system prompt lives in the SQLite DB (authored in the control panel). There is
        // no code-baked default: a fresh DB has no system prompt until one is saved. Read once at
        // agent-build time; a panel edit applies on restart.
        var instructions = services.GetRequiredService<IPromptStore>().GetActiveContent(PromptKind.System);

        // The agent's name MUST equal the registration key (see Program.cs AddAIAgent).
        var agent = chatClient.AsAIAgent(instructions: instructions, name: Name, tools: tools);

        // Instrument with OpenTelemetry (MAF builder): spans for the agent run, the model call
        // (token usage), and each tool/function invocation, emitted on ActivitySourceName.
        // EnableSensitiveData records prompts + tool arguments; gated by the config flag (off in
        // production), mirroring the OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT env var.
        return agent
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: ObservabilityOptions.ActivitySourceName,
                configure: telemetry => telemetry.EnableSensitiveData = observability.CaptureMessageContent)
            // Record each tool invocation to the activity feed (all channels), name only.
            .Use(ToolCallActivity.Middleware(recorder))
            .Build();
    }
}
