using System.ClientModel;
using Azure.AI.OpenAI;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Erda.Agents.Orchestration;

/// <summary>
/// Builds the browser sub-agent and exposes it to the orchestrator as the single <c>browse_web</c>
/// tool (agent-as-tool, like <see cref="Erda.Agents.Workflows.VoiceMemoWorkflow.CreateTool"/>). The
/// sub-agent runs its own multi-step loop with the Playwright MCP tools, so the page snapshots stay
/// out of the orchestrator's context. Its model is <c>Browser:Deployment</c>, defaulting to the
/// orchestrator's <c>ChatDeployment</c>.
/// </summary>
public static class BrowserAgent
{
    public const string ToolName = "browse_web";

    public const string ToolDescription =
        "Perform a web task in a real browser (navigate, read, interact) and return the result. " +
        "Provide the task in plain language, e.g. 'open example.com and tell me the main heading'.";

    private const string SystemPrompt =
        "You control a real web browser through tools. Work step by step: take a snapshot to see the " +
        "page, then act (navigate/click/type), then snapshot again. Prefer the accessibility snapshot " +
        "over screenshots for deciding actions. When you have the answer, state it concisely. If a page " +
        "blocks you (captcha, login you cannot complete), stop and say so rather than guessing.";

    /// <summary>True when the feature is on and the MCP actually connected with at least one tool.</summary>
    public static bool ShouldExpose(IBrowserMcp mcp) => mcp.Enabled && mcp.Tools.Count > 0;

    /// <summary>
    /// Build the <c>browse_web</c> function, or null when <see cref="ShouldExpose"/> is false.
    /// Requires Azure credentials (same as the orchestrator); returns null if unconfigured.
    /// </summary>
    public static AIFunction? TryCreateTool(IServiceProvider services)
    {
        var mcp = services.GetRequiredService<IBrowserMcp>();
        if (!ShouldExpose(mcp)) return null;

        var configuration = services.GetRequiredService<IConfiguration>();
        var erda = services.GetRequiredService<IOptions<ErdaOptions>>().Value;
        var browser = services.GetRequiredService<IOptions<BrowserOptions>>().Value;

        var endpoint = configuration["AZURE_OPENAI_ENDPOINT"];
        var apiKey = configuration["AZURE_OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey)) return null;

        var deployment = string.IsNullOrWhiteSpace(browser.Deployment) ? erda.ChatDeployment : browser.Deployment!;

        ChatClient chat = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey))
            .GetChatClient(deployment);

        AIAgent agent = chat.AsAIAgent(
            instructions: SystemPrompt,
            name: "browser",
            tools: [.. mcp.Tools]);

        return agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = ToolName,
            Description = ToolDescription,
        });
    }
}
