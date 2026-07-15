namespace Erda.Core.Configuration;

/// <summary>
/// Options for the agentic browser feature (Playwright MCP). Bound from the <c>Erda:Browser</c>
/// configuration section. Off by default — when <see cref="Enabled"/> is false the MCP child is
/// never launched and the <c>browse_web</c> tool is not registered.
/// <para>
/// The settable members are configuration: the <see cref="Enabled"/>/<see cref="ShowWindow"/>
/// switches, the optional sub-agent <see cref="Deployment"/>, and <see cref="UserDataDir"/>/
/// <see cref="OutputDir"/> (required when enabled — see <c>BrowserOptionsValidator</c>). The rest are
/// fixed mechanics expressed as read-only constants, so there is no default to set or forget.
/// </para>
/// </summary>
public sealed class BrowserOptions
{
    public const string SectionName = "Erda:Browser";

    /// <summary>Master switch. When false, no MCP child process and no <c>browse_web</c> tool.</summary>
    public bool Enabled { get; set; }

    /// <summary>Model id for the browser sub-agent. Null/blank => use ErdaOptions.ChatModel.</summary>
    public string? Deployment { get; set; }

    /// <summary>Run the browser <b>headful</b> instead of headless. Absent ⇒ false ⇒ headless. Set
    /// <c>Erda__Browser__ShowWindow=true</c> on the dev Mac to watch the agent browse — and <b>in
    /// production too</b>: the container runs under <c>xvfb-run</c> (a virtual display), and headful is
    /// required to get past sites that hard-block headless Chromium (Cloudflare on cardmarket.com returns
    /// an "Attention Required" challenge to headless, but lets the same automated browser through
    /// headful).</summary>
    public bool ShowWindow { get; set; }

    /// <summary>Persistent profile directory (the logged-in session) — on the browser-data volume in
    /// the container. Required when <see cref="Enabled"/>.</summary>
    public string UserDataDir { get; set; } = "";

    /// <summary>Directory the MCP writes output files (screenshots) to (<c>--output-dir</c>). Prod = the
    /// shared <c>/media</c> volume the WhatsApp bridge sends from; dev points at a project folder.
    /// Required when <see cref="Enabled"/>.</summary>
    public string OutputDir { get; set; } = "";

    // --- Fixed mechanics (constants, not configuration) -------------------------------------------

    /// <summary>Executable that launches the MCP server (stdio).</summary>
    public string McpCommand => "npx";

    /// <summary>Base arguments for <see cref="McpCommand"/>. Pinned MCP version + browser selection.
    /// <c>--browser chromium</c> is required: the MCP otherwise defaults to the <c>chrome</c> channel
    /// (branded Google Chrome), which the runtime image doesn't install and which has no ARM64 Linux
    /// build — leaving it off fails with "Chromium distribution 'chrome' is not found". This selects the
    /// bundled Chromium the Dockerfile installs. <c>--no-sandbox</c> is required to launch Chromium in
    /// the container (per the Playwright MCP Docker docs). <c>--image-responses omit</c> stops the
    /// screenshot tool from returning the PNG inline as base64 — a full-page screenshot is ~1.2M tokens
    /// and blew the sub-agent's context (<c>context_length_exceeded</c>); the file is still written to
    /// disk, which is all Erda needs (it sends the file via send_image). <c>--headless</c> is appended by
    /// the runner unless <see cref="ShowWindow"/> is set.</summary>
    public string[] McpArgs => ["@playwright/mcp@0.0.75", "--browser", "chromium", "--no-sandbox", "--image-responses", "omit"];

    /// <summary>Upper bound on tool calls inside a single browse_web run, to bound a runaway loop.</summary>
    public int MaxSteps => 40;

    /// <summary>Executable that runs the 1Password CLI (resolves <c>op://…</c> references and lists
    /// the <see cref="OnePasswordVault"/> items). On PATH in the runtime image.</summary>
    public string OpCommand => "op";

    /// <summary>The single 1Password vault Erda may read. It is the account registry AND the
    /// allow-list: only logins in this vault can be used. The service-account token is scoped
    /// read-only to it. References outside this vault are refused by the resolver.</summary>
    public string OnePasswordVault => "Erda";
}
