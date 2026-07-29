namespace Erda.Core.Data;

/// <summary>
/// One archived voice memo uploaded via the HTTP <c>/upload</c> endpoint (the iOS Shortcut path).
/// Records provenance (when, the display filename), the stored audio file for playback, and the
/// resulting Obsidian note — so the panel can show a durable archive of what was sent. WhatsApp voice
/// notes are deliberately NOT recorded here; only API uploads create a row (in <c>UploadIntake</c>).
/// The audio file itself lives in the archive directory (durable, alongside the DB), not the media
/// temp dir; deleting a row deletes that file but never the Obsidian note.
/// </summary>
public sealed class VoiceMemoRow
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Human-facing name for the memo (original upload filename, or a timestamped fallback).</summary>
    public string FileName { get; set; } = "";

    /// <summary>The audio file's name inside the archive directory — used to serve and delete it.</summary>
    public string AudioFileName { get; set; } = "";

    /// <summary>Stored audio size in bytes.</summary>
    public long AudioBytes { get; set; }

    /// <summary>Vault-relative path of the produced note (e.g. <c>1 Inbox/2026-…​.md</c>); null until filed.</summary>
    public string? NotePath { get; set; }

    /// <summary>One of: <c>pending</c>, <c>filed</c>, <c>raw</c> (unformatted fallback), <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";
}
