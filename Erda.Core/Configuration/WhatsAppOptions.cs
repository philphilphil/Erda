namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the WhatsApp channel (bound from the "WhatsApp" config section).
/// Erda talks to a local whatsmeow "bridge" sidecar over localhost HTTP; the bridge holds the
/// WhatsApp socket. The shared secret authenticates both hops. The owner number is the ONLY
/// sender Erda answers, and the target for proactive messages.
/// </summary>
public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Master switch. When false, the inbound endpoint is not mapped. The four settings
    /// below are required (validated at startup) only when this is true.</summary>
    public bool Enabled { get; set; }

    /// <summary>Phil's main WhatsApp number in international form, e.g. "+49 151 2345 6789". Required when enabled.</summary>
    public string OwnerNumber { get; set; } = "";

    /// <summary>Base URL of the bridge's HTTP server (its <c>/send</c> endpoint lives here). Required when enabled.</summary>
    public string BridgeUrl { get; set; } = "";

    /// <summary>Shared secret sent as the <c>X-Bridge-Secret</c> header on both hops. Required when enabled.</summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>Directory the bridge drops downloaded media into; Erda reads then deletes from here. Required when enabled.</summary>
    public string MediaTempDir { get; set; } = "";

    /// <summary>
    /// Dev-routing keyword for running a Development instance alongside Production on the same
    /// WhatsApp account. A Development instance answers ONLY messages whose text starts with this
    /// prefix (and strips it); a Production instance IGNORES those (the dev instance takes them).
    /// Empty/whitespace disables the gating entirely (the instance answers everything, as before).
    /// </summary>
    public string DevPrefix { get; set; } = "@dev";
}
