using System.ClientModel;
using OpenAI;
using OpenAI.Responses;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.Services.OnePassword;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Agents;

/// <summary>
/// Builds the browser sub-agent and exposes it to the orchestrator as the single <c>browse_web</c>
/// tool (agent-as-tool, like <see cref="Erda.Agents.Workflows.VoiceMemoWorkflow.CreateTool"/>). The
/// sub-agent runs its own multi-step loop with the Playwright MCP tools, so the page snapshots stay
/// out of the orchestrator's context. Its model is <c>Browser:Deployment</c>, defaulting to the
/// orchestrator's <c>ChatModel</c>.
/// </summary>
public static class BrowserAgent
{
    public const string ToolName = "browse_web";

    public const string ToolDescription =
        "Perform a web task in a real browser (navigate, read, interact, or take a screenshot) and " +
        "return the result. Provide the task in plain language, e.g. 'open example.com and tell me the " +
        "main heading'. For a screenshot, say so explicitly (e.g. 'open example.com and take a full-page " +
        "screenshot'); the browser saves the image to the media directory and returns its absolute file " +
        "path, which you can then send to Phil with send_image. Use this for anything that needs a " +
        "live page rendered.";

    // outputDir is injected so the screenshot guidance names the one writable, allow-listed directory
    // (the MCP's --output-dir). A bare/relative screenshot filename resolves against the MCP's CWD
    // (/app, read-only for the 1000:1000 container) and fails with EACCES; only an ABSOLUTE path under
    // outputDir works. See BrowserOptions.McpArgs for why image responses are omitted.
    private static string BuildSystemPrompt(string outputDir) =>
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
        "SCREENSHOTS: when asked for a screenshot, navigate to the page and let it settle, then call the " +
        "screenshot tool with an ABSOLUTE filename under " + outputDir + " — e.g. \"" + outputDir +
        "/screenshot.png\". A bare or relative name is rejected (the tool writes to a read-only " +
        "directory by default), so always pass the full path under " + outputDir + ". Capture the full " +
        "page when asked. Then report that exact absolute path in your final answer so the screenshot " +
        "can be sent on to Phil.";

    /// <summary>True when the feature is on and the MCP actually connected with at least one tool.</summary>
    public static bool ShouldExpose(IBrowserMcp mcp) => mcp.Enabled && mcp.Tools.Count > 0;

    /// <summary>
    /// Build the <c>browse_web</c> function, or null when <see cref="ShouldExpose"/> is false.
    /// Uses the same Responses endpoint as the orchestrator.
    /// </summary>
    public static AIFunction? TryCreateTool(IServiceProvider services)
    {
        var mcp = services.GetRequiredService<IBrowserMcp>();
        if (!ShouldExpose(mcp)) return null;

        var erda = services.GetRequiredService<IOptions<ErdaOptions>>().Value;
        var browser = services.GetRequiredService<IOptions<BrowserOptions>>().Value;
        var observability = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        // Endpoint settings are validated at startup, so they're guaranteed present here.
        // falls back to the orchestrator's model (ChatModel) when no browser-specific one is set
        var deployment = string.IsNullOrWhiteSpace(browser.Deployment) ? erda.ChatModel : browser.Deployment!;

        var opCli = services.GetRequiredService<IOpCli>();
        var secretResolver = services.GetRequiredService<IOpSecretResolver>();

        // Same Responses client as the orchestrator (see ErdaAgent), pointed at the local
        // OpenAI-compatible endpoint. The sub-agent's model is browser.Deployment (falls back to
        // ChatModel above); the key defaults to "local" since the loopback proxy needs no real one.
#pragma warning disable OPENAI001 // Responses surface is [Experimental]
        OpenAI.Responses.ResponsesClient chat = new OpenAI.Responses.ResponsesClient(
            credential: new ApiKeyCredential(string.IsNullOrWhiteSpace(erda.ChatApiKey) ? "local" : erda.ChatApiKey),
            options: new OpenAIClientOptions { Endpoint = new Uri(erda.ChatBaseUrl) });
#pragma warning restore OPENAI001

        var tools = new List<AITool>(mcp.Tools) { FindLogin.CreateTool(opCli, browser.OnePasswordVault) };

        // The Playwright snapshots are huge and accumulate; a reducer trims stale ones below the
        // function-invocation loop so the sub-agent's context stays bounded (it previously blew the
        // model's window with context_length_exceeded), and StepLimit caps a runaway loop at MaxSteps.
        var reducer = new BrowserSnapshotReducer();

#pragma warning disable OPENAI001 // ResponsesClient.AsAIAgent is [Experimental]
        AIAgent agent = chat.AsAIAgent(
                model: deployment,
                instructions: BuildSystemPrompt(browser.OutputDir),
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
#pragma warning restore OPENAI001

        return agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = ToolName,
            Description = ToolDescription,
        });
    }
}
