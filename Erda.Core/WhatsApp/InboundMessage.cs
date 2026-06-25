namespace Erda.Core.WhatsApp;

/// <summary>The kind of WhatsApp message, derived from the bridge's "type" field.</summary>
public enum InboundKind
{
    Text,
    Audio,
    Image,
    Unknown,
}

/// <summary>
/// The JSON payload the whatsmeow bridge POSTs to <c>/channel/whatsapp/in</c>. Field names match
/// the bridge contract (camelCase on the wire; bound case-insensitively by ASP.NET).
/// </summary>
public sealed record InboundMessage
{
    /// <summary>Sender JID, e.g. "4915123456789@s.whatsapp.net".</summary>
    public string From { get; init; } = "";

    /// <summary>JID to reply to (normally the same as <see cref="From"/>).</summary>
    public string Chat { get; init; } = "";

    /// <summary>"text" | "audio" | "image".</summary>
    public string Type { get; init; } = "text";

    /// <summary>Text body for "text", or the caption for "image".</summary>
    public string? Text { get; init; }

    /// <summary>Absolute path to downloaded media (audio/image), on this same host.</summary>
    public string? MediaPath { get; init; }

    /// <summary>Media MIME type, e.g. "audio/ogg; codecs=opus" or "image/jpeg".</summary>
    public string? MimeType { get; init; }

    /// <summary>
    /// True when the audio is a WhatsApp push-to-talk voice note (recorded in WhatsApp), as opposed to
    /// a shared audio file. Drives routing: PTT notes are conversational; shared Apple Voice Memo
    /// files go to the inbox pipeline. Absent/false for non-audio or older bridge builds.
    /// </summary>
    public bool Ptt { get; init; }

    public string? MessageId { get; init; }

    public long Timestamp { get; init; }

    public InboundKind Kind => Type?.Trim().ToLowerInvariant() switch
    {
        "text" => InboundKind.Text,
        "audio" => InboundKind.Audio,
        "image" => InboundKind.Image,
        _ => InboundKind.Unknown,
    };
}
