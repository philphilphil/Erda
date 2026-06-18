using System.Security.Cryptography;
using System.Text;
using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erda.Core.Upload;

/// <summary>The outcome of an upload, mapped to an HTTP status by the endpoint.</summary>
public enum UploadOutcome
{
    NoFile,
    TooLarge,
    Accepted,
}

/// <summary>
/// Turns an authenticated HTTP audio upload into a WhatsApp-pipeline job. It saves the bytes into the
/// shared media temp dir and enqueues a synthesized <see cref="InboundMessage"/> (audio/mp4, addressed
/// to the owner) onto the existing <see cref="WhatsAppInboundQueue"/> — so the regular inbound worker
/// transcribes it once, runs the memo pipeline (→ 1 Inbox/), replies over WhatsApp, and deletes the
/// media. This is deliberately the same path an Apple Voice Memo shared via WhatsApp takes.
///
/// HTTP concerns (multipart parsing, status codes, request-size limits) stay in the endpoint; this
/// type is host-agnostic so it can be unit-tested without a web server.
/// </summary>
public sealed class UploadIntake(
    IOptions<UploadOptions> uploadOptions,
    IOptions<WhatsAppOptions> whatsAppOptions,
    WhatsAppInboundQueue queue,
    ILogger<UploadIntake> logger)
{
    /// <summary>
    /// Constant-time check of an <c>Authorization: Bearer &lt;key&gt;</c> header against the configured
    /// key. Returns false when the key is unset (defence-in-depth: the route is not even mapped then),
    /// when the header is missing, or when the scheme/token does not match.
    /// </summary>
    public bool IsAuthorized(string? authorizationHeader)
    {
        var apiKey = uploadOptions.Value.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
            return false;

        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(authorizationHeader) ||
            !authorizationHeader.StartsWith(scheme, StringComparison.Ordinal))
            return false;

        var provided = Encoding.UTF8.GetBytes(authorizationHeader[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(apiKey);
        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    /// <summary>
    /// Validates size, persists the audio to the media temp dir, and enqueues the pipeline job. Assumes
    /// the caller has already authorized the request (see <see cref="IsAuthorized"/>).
    /// </summary>
    public async Task<UploadOutcome> IngestAsync(long length, Stream content, CancellationToken cancellationToken = default)
    {
        if (length <= 0)
            return UploadOutcome.NoFile;
        if (length > (long)uploadOptions.Value.MaxUploadMb * 1024 * 1024)
            return UploadOutcome.TooLarge;

        var mediaDir = whatsAppOptions.Value.MediaTempDir;
        Directory.CreateDirectory(mediaDir);
        var path = Path.Combine(mediaDir, $"upload_{Guid.NewGuid():N}.m4a");

        await using (var file = File.Create(path))
            await content.CopyToAsync(file, cancellationToken);

        var ownerJid = WhatsAppJid.FromNumber(whatsAppOptions.Value.OwnerNumber);
        await queue.EnqueueAsync(new InboundMessage
        {
            From = ownerJid,
            Chat = ownerJid,
            Type = "audio",
            MimeType = "audio/mp4", // Apple Voice Memo (.m4a) → IsSharedVoiceMemo branch in the channel service
            MediaPath = path,
            MessageId = $"upload_{Guid.NewGuid():N}",
            // Timestamp 0: an HTTP upload is a one-shot request, never a replayed bridge message, so it
            // must not be subject to the channel's replay-drop guard (Timestamp > 0 && < process-start).
            Timestamp = 0,
        }, cancellationToken);

        logger.LogInformation("Upload accepted: {Bytes} bytes saved to {Path}, enqueued for the voice-memo pipeline.", length, path);
        return UploadOutcome.Accepted;
    }
}
