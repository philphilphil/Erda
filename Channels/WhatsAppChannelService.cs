using Erda.Configuration;
using Erda.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Channels;

/// <summary>
/// Turns an inbound WhatsApp message into an agent turn and sends the reply back via the bridge.
/// Enforces the owner whitelist, dispatches by message type (text / voice / image), and cleans up
/// downloaded media afterwards.
/// </summary>
public sealed class WhatsAppChannelService(
    IOptions<WhatsAppOptions> options,
    IAgentResponder responder,
    ITranscriber transcriber,
    IWhatsAppSender sender,
    ILogger<WhatsAppChannelService> logger)
{
    public async Task ProcessAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        var o = options.Value;

        if (!WhatsAppJid.IsOwner(o.OwnerNumber, message.From) || WhatsAppJid.IsGroup(message.Chat))
        {
            logger.LogWarning("Dropping WhatsApp message from non-owner/group sender {From}.", message.From);
            return;
        }

        var replyTarget = string.IsNullOrEmpty(message.Chat) ? message.From : message.Chat;

        try
        {
            var messages = await BuildMessagesAsync(message, cancellationToken);
            if (messages is null)
            {
                await sender.SendAsync(replyTarget, "Sorry — I can only handle text, voice notes, and images right now.", cancellationToken);
                return;
            }

            var reply = await responder.RespondAsync(messages, cancellationToken);
            await sender.SendAsync(replyTarget, string.IsNullOrWhiteSpace(reply) ? "(no response)" : reply, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling WhatsApp message of type {Type}.", message.Type);
            await sender.SendAsync(replyTarget, $"⚠️ Something went wrong: {ex.Message}", cancellationToken);
        }
        finally
        {
            CleanupMedia(message, o.MediaTempDir);
        }
    }

    /// <summary>Builds the chat message(s) for the agent, or null for an unsupported/empty message.</summary>
    private async Task<IReadOnlyList<ChatMessage>?> BuildMessagesAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case InboundKind.Text:
            {
                var text = message.Text?.Trim();
                return string.IsNullOrEmpty(text) ? null : [new ChatMessage(ChatRole.User, text)];
            }

            case InboundKind.Audio:
            {
                if (string.IsNullOrEmpty(message.MediaPath) || !File.Exists(message.MediaPath))
                    return null;
                var transcript = await transcriber.TranscribeAsync(message.MediaPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(transcript))
                    return null;
                // Hand the agent the transcript as the user's message. The agent can act on it
                // (answer, or save to the vault via its tools / process_voice_memo) as asked.
                return [new ChatMessage(ChatRole.User, $"[Voice note transcript]\n{transcript}")];
            }

            case InboundKind.Image:
            {
                if (string.IsNullOrEmpty(message.MediaPath) || !File.Exists(message.MediaPath))
                    return null;
                var bytes = await File.ReadAllBytesAsync(message.MediaPath, cancellationToken);
                var mime = string.IsNullOrWhiteSpace(message.MimeType) ? "image/jpeg" : message.MimeType.Split(';')[0].Trim();
                var caption = string.IsNullOrWhiteSpace(message.Text) ? "Describe this image." : message.Text!.Trim();
                IList<AIContent> content = [new TextContent(caption), new DataContent(bytes, mime)];
                return [new ChatMessage(ChatRole.User, content)];
            }

            default:
                return null;
        }
    }

    /// <summary>Best-effort delete of downloaded media, but only inside the configured temp dir.</summary>
    private void CleanupMedia(InboundMessage message, string mediaTempDir)
    {
        if (string.IsNullOrEmpty(message.MediaPath))
            return;
        try
        {
            var full = Path.GetFullPath(message.MediaPath);
            var root = Path.GetFullPath(mediaTempDir);
            if (full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full))
                File.Delete(full);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete media file {Path}.", message.MediaPath);
        }
    }
}
