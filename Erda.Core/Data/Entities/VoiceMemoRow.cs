namespace Erda.Core.Data;

/// <summary>Where an archived piece of inbound audio came from.</summary>
public enum VoiceMemoSource
{
    /// <summary>The HTTP <c>/upload</c> endpoint (the iOS Shortcut path).</summary>
    Upload,

    /// <summary>An Apple Voice Memo file shared through WhatsApp → the <c>1 Inbox/</c> memo pipeline.</summary>
    AppleMemo,

    /// <summary>A WhatsApp-recorded voice note → a conversational agent turn, no note.</summary>
    WhatsAppVoice,
}

/// <summary>How far an archived memo got, and what it produced.</summary>
public enum VoiceMemoStatus
{
    /// <summary>Archived, pipeline not finished yet.</summary>
    Pending,

    /// <summary>Filed as a structured note in the vault.</summary>
    Filed,

    /// <summary>Unformatted fallback — the raw transcript was saved because the reasoner was down.</summary>
    Raw,

    /// <summary>Transcription/processing could not produce anything.</summary>
    Failed,

    /// <summary>Answered by the agent — a conversational turn that produced no note.</summary>
    Answered,
}

/// <summary>
/// The DB/wire spelling of the voice-memo enums: lowercase kebab (<c>apple-memo</c>,
/// <c>whatsapp-voice</c>, …). Shared by the EF value conversions (so the column stays readable TEXT
/// and the values written before the enums existed keep working) and by the panel JSON.
/// </summary>
public static class VoiceMemoWire
{
    public static string ToWire(this VoiceMemoSource source) => source switch
    {
        VoiceMemoSource.AppleMemo => "apple-memo",
        VoiceMemoSource.WhatsAppVoice => "whatsapp-voice",
        _ => "upload",
    };

    /// <summary>Unknown values fall back to <see cref="VoiceMemoSource.Upload"/> — every row that predates the column came from <c>/upload</c>.</summary>
    public static VoiceMemoSource ParseSource(string value) => value switch
    {
        "apple-memo" => VoiceMemoSource.AppleMemo,
        "whatsapp-voice" => VoiceMemoSource.WhatsAppVoice,
        _ => VoiceMemoSource.Upload,
    };

    public static string ToWire(this VoiceMemoStatus status) => status switch
    {
        VoiceMemoStatus.Filed => "filed",
        VoiceMemoStatus.Raw => "raw",
        VoiceMemoStatus.Failed => "failed",
        VoiceMemoStatus.Answered => "answered",
        _ => "pending",
    };

    /// <summary>Unknown values fall back to <see cref="VoiceMemoStatus.Pending"/>, so the startup sweep resolves them.</summary>
    public static VoiceMemoStatus ParseStatus(string value) => value switch
    {
        "filed" => VoiceMemoStatus.Filed,
        "raw" => VoiceMemoStatus.Raw,
        "failed" => VoiceMemoStatus.Failed,
        "answered" => VoiceMemoStatus.Answered,
        _ => VoiceMemoStatus.Pending,
    };
}

/// <summary>
/// One archived piece of inbound voice audio: an HTTP <c>/upload</c> (the iOS Shortcut path), an Apple
/// Voice Memo shared through WhatsApp, or a WhatsApp-recorded voice note — see
/// <see cref="VoiceMemoSource"/>. Records provenance (when, the display filename, the source), the
/// stored audio file for playback, and what it produced: the resulting Obsidian note for the memo
/// pipelines, or the transcript for an agent turn that only replied.
/// The audio file itself lives in the archive directory (durable, alongside the DB), not the media
/// temp dir; deleting a row deletes that file but never the Obsidian note.
/// </summary>
public sealed class VoiceMemoRow
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Human-facing name for the memo (original upload filename, or a timestamped fallback).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Which inbound path the audio arrived on. Stored as lowercase-kebab TEXT.</summary>
    public VoiceMemoSource Source { get; set; } = VoiceMemoSource.Upload;

    /// <summary>The audio file's name inside the archive directory — used to serve and delete it.</summary>
    public string AudioFileName { get; set; } = "";

    /// <summary>Stored audio size in bytes.</summary>
    public long AudioBytes { get; set; }

    /// <summary>Vault-relative path of the produced note (e.g. <c>1 Inbox/2026-…​.md</c>); null until filed.</summary>
    public string? NotePath { get; set; }

    /// <summary>
    /// The transcript, kept only for <see cref="VoiceMemoStatus.Answered"/> rows (agent turns produce no
    /// note, so this is the only record of what was said). Null for the memo pipelines, which link a note.
    /// </summary>
    public string? Transcript { get; set; }

    /// <summary>
    /// Stored as lowercase-kebab TEXT. Rows still <see cref="VoiceMemoStatus.Pending"/> from a previous
    /// process are swept to <see cref="VoiceMemoStatus.Failed"/> at startup — the inbound queue is
    /// in-memory, so they can never be processed.
    /// </summary>
    public VoiceMemoStatus Status { get; set; } = VoiceMemoStatus.Pending;
}
