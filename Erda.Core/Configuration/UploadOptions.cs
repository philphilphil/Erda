namespace Erda.Core.Configuration;

/// <summary>
/// Settings for the HTTP upload endpoint (bound from the "Upload" config section). An iOS Shortcut
/// (or any client) POSTs an audio file to <c>/upload</c> with a bearer token; the file is fed into the
/// SAME Apple-Voice-Memo pipeline as a WhatsApp share (transcribe → Codex → 1 Inbox/), and the result
/// is delivered back over WhatsApp to the owner. The pipeline + reply therefore require the WhatsApp
/// channel to be enabled (enforced at startup when this feature is on).
/// </summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>Master switch. When false, <c>POST /upload</c> is not mapped. <see cref="ApiKey"/> is
    /// required (validated at startup) only when this is true.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bearer token the caller must present as <c>Authorization: Bearer &lt;key&gt;</c>. Compared
    /// in constant time. Required when <see cref="Enabled"/>; a blank key rejects every request.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Maximum accepted upload size in megabytes; larger bodies get a 413. Required (must be
    /// positive) when <see cref="Enabled"/> — no in-code default, like every other setting;
    /// <c>.env.example</c> ships 50 as the recommended value.</summary>
    public int MaxUploadMb { get; set; }
}
