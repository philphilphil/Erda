using System.ClientModel;
using OpenAI;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.Services.OnePassword;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Erda.Agents;

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
        "Perform a web task in a real browser (navigate, read, interact, or take a screenshot) and " +
        "return the result. Provide the task in plain language, e.g. 'open example.com and tell me the " +
        "main heading'. For a screenshot, say so explicitly (e.g. 'open example.com and take a full-page " +
        "screenshot'); the browser saves the image to the media directory and returns its absolute file " +
        "path, which you can then send to Phil with send_image. Use this — not consult_codex — for " +
        "anything that needs a live page rendered.";

    private const string SystemPrompt =
        "You control a real web browser through tools. Work step by step: take a snapshot to see the " +
        "page, then act (navigate/click/type), then snapshot again. Prefer the accessibility snapshot " +
        "over screenshots for deciding actions. When you have the answer, state it concisely.\n\n" +
        "LOGGING IN: if a page requires sign-in, call find_login with the site's domain. It returns " +
        "1Password references (op://…) — type those references verbatim into the username and password " +
        "fields; they resolve to the real credentials securely, so never ask for or guess a password. " +
        "If the site then asks for a one-time code / 2FA, type the one-time-password reference it gave " +
        "you. If find_login says there is no login, or the site shows a captcha or a push/SMS/email " +
        "challenge you cannot complete, STOP and report clearly that you are blocked and why — do not " +
        "guess credentials or codes.\n\n" +
        "SCREENSHOTS: when asked for a screenshot, navigate to the page and let it settle, then take the " +
        "screenshot (full page when asked, otherwise the visible viewport). The image is saved to the " +
        "output directory — report the absolute path of the saved file in your final answer so it can be " +
        "sent on to Phil.";

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

        var creds = services.GetRequiredService<IOptions<CredentialsOptions>>().Value;
        var erda = services.GetRequiredService<IOptions<ErdaOptions>>().Value;
        var browser = services.GetRequiredService<IOptions<BrowserOptions>>().Value;
        var observability = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        // Credentials are validated at startup, so they're guaranteed present here.
        // falls back to the orchestrator's deployment (ChatDeployment) when no browser-specific one is set
        var deployment = string.IsNullOrWhiteSpace(browser.Deployment) ? erda.ChatDeployment : browser.Deployment!;

        var opCli = services.GetRequiredService<IOpCli>();
        var secretResolver = services.GetRequiredService<IOpSecretResolver>();

        // Same OpenAI-compatible /openai/v1 client as the orchestrator (see ErdaAgent) — no
        // Azure.AI.OpenAI, no api-version. The sub-agent's model is browser.Deployment (falls back to
        // ChatDeployment above).
        ChatClient chat = new ChatClient(
            model: deployment,
            credential: new ApiKeyCredential(creds.AzureOpenAIApiKey),
            options: new OpenAIClientOptions { Endpoint = new Uri(creds.AzureOpenAIEndpoint) });

        var tools = new List<AITool>(mcp.Tools) { FindLogin.CreateTool(opCli, browser.OnePasswordVault) };

        // The Playwright snapshots are huge and accumulate; a reducer trims stale ones below the
        // function-invocation loop so the sub-agent's context stays bounded (it previously blew the
        // model's window with context_length_exceeded), and StepLimit caps a runaway loop at MaxSteps.
        var reducer = new BrowserSnapshotReducer();

        AIAgent agent = chat.AsAIAgent(
                instructions: SystemPrompt,
                name: "browser",
                tools: tools,
                clientFactory: inner => inner.AsBuilder().UseChatReducer(reducer).Build())
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: ObservabilityOptions.ActivitySourceName,
                configure: telemetry => telemetry.EnableSensitiveData = observability.CaptureMessageContent)
            // Secret injection runs INSIDE OpenTelemetry (added after it): it swaps op:// references for
            // real values just before the MCP type call and restores the reference in a finally, so the
            // OTel span records only the reference — never the resolved secret. See SecretInjection.
            .Use(SecretInjection.Middleware(secretResolver))
            .Use(StepLimit.Middleware(browser.MaxSteps))
            // NOTE: intentionally NOT adding ToolCallActivity.Middleware here — the orchestrator already
            // records the top-level browse_web call; recording every inner navigate/click would flood the
            // LAN activity feed. Granular browser steps live in OTel/Seq instead.
            .Build();

        return agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = ToolName,
            Description = ToolDescription,
        });
    }
}
