namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the macOS ErdaBridge integration (bound from the "AppleBridge" config section): a
/// small, LAN-only HTTP API run by a companion app on Phil's Mac that lets Erda create, list and
/// complete tasks in explicitly allowlisted Apple Reminders lists. See <c>macos-bridge/</c> for the
/// bridge itself; <see cref="Services.IAppleBridgeClient"/> is the .NET client for this API.
/// </summary>
public sealed class AppleBridgeOptions
{
    public const string SectionName = "AppleBridge";

    /// <summary>Master switch. When false, no Apple Reminders tools are registered on the agent.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the ErdaBridge app on the Mac, e.g. <c>http://192.168.178.106:17832</c>.
    /// Required when <see cref="Enabled"/>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Bearer token the bridge requires on every request, including status checks. Required
    /// when <see cref="Enabled"/>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>HTTP request timeout in seconds. The Mac is frequently asleep or off the LAN, so this
    /// should be short enough that a tool call fails fast with a readable message instead of hanging
    /// the agent turn. Required (must be positive) when <see cref="Enabled"/> — no in-code default,
    /// like every other setting.</summary>
    public int TimeoutSeconds { get; set; }
}
