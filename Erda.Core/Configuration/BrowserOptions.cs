namespace Erda.Core.Configuration;

/// <summary>
/// Options for the agentic browser feature (Playwright MCP). Bound from the <c>Erda:Browser</c>
/// configuration section. Off by default — when <see cref="Enabled"/> is false the MCP child is
/// never launched and the <c>browse_web</c> tool is not registered.
/// </summary>
public sealed class BrowserOptions
{
    public const string SectionName = "Erda:Browser";

    /// <summary>Master switch. When false, no MCP child process and no <c>browse_web</c> tool.</summary>
    public bool Enabled { get; set; }

    /// <summary>Azure AI Foundry deployment for the browser sub-agent. Null => use ErdaOptions.ChatDeployment.</summary>
    public string? Deployment { get; set; }

    /// <summary>Executable that launches the MCP server (stdio).</summary>
    public string McpCommand { get; set; } = "npx";

    /// <summary>Base arguments for <see cref="McpCommand"/>. Pinned MCP version + persistent profile.
    /// <c>--headless</c> is appended by the runner when <see cref="Headless"/> is true (so local dev can
    /// drop it and watch the browser).</summary>
    public string[] McpArgs { get; set; } =
        ["@playwright/mcp@0.0.75", "--user-data-dir", "/data/browser"];

    /// <summary>Run Chromium headless. Default true (the Jetson has no display). Set
    /// <c>Erda__Browser__Headless=false</c> on the dev Mac to watch the agent browse in a real window.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>Persistent profile directory (kept on the browser-data volume) — the logged-in session.</summary>
    public string UserDataDir { get; set; } = "/data/browser";

    /// <summary>Upper bound on tool calls inside a single browse_web run, to bound a runaway loop.</summary>
    public int MaxSteps { get; set; } = 40;
}
