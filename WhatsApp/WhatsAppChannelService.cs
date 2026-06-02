using System.Diagnostics;
using Erda.Agents;
using Erda.Configuration;
using Erda.Services;
using Erda.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.WhatsApp;

/// <summary>
/// Turns an inbound WhatsApp message into an agent turn and sends the reply back via the bridge.
/// Enforces the owner whitelist, dispatches by message type (text / voice / image), and cleans up
/// downloaded media afterwards.
///
/// Audio dispatch: Apple Voice Memos shared from iOS arrive as .m4a (audio/mp4) and are routed
/// directly to <see cref="MemoProcessor"/> (structured memo → 1 Inbox/), bypassing the agent.
/// WhatsApp-native voice notes (audio/ogg) go to the agent as a transcript for conversational handling.
/// </summary>
public sealed class WhatsAppChannelService(
    IOptions<WhatsAppOptions> options,
    IAgentResponder responder,
    ITranscriber transcriber,
    IMemoProcessor memoProcessor,
    IWhatsAppSender sender,
    IHostEnvironment hostEnvironment,
    CurrentTimeContext timeContext,
    ILogger<WhatsAppChannelService> logger)
{
    // Drop any message the bridge replays from before this process started.
    private readonly long _startedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public async Task ProcessAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        var o = options.Value;

        if (message.Timestamp > 0 && message.Timestamp < _startedAtUnix)
        {
            logger.LogDebug("Dropping replayed message {Id} (ts={Ts}, started={Start}).",
                message.MessageId, message.Timestamp, _startedAtUnix);
            return;
        }

        if (!WhatsAppJid.IsOwner(o.OwnerNumber, message.From) || WhatsAppJid.IsGroup(message.Chat))
        {
            logger.LogWarning("Dropping WhatsApp message from non-owner/group sender {From}.", message.From);
            return;
        }

        var replyTarget = string.IsNullOrEmpty(message.Chat) ? message.From : message.Chat;

        // Dev/prod routing so a Development instance can run alongside Production on the same
        // WhatsApp account without double-replying. A Development instance handles ONLY messages
        // whose text starts with the dev prefix (stripped before processing); Production ignores
        // those. An empty prefix disables the gating (the instance answers everything).
        var prefix = o.DevPrefix?.Trim();
        if (!string.IsNullOrEmpty(prefix))
        {
            var body = message.Text?.TrimStart();
            var targeted = body is not null && body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (hostEnvironment.IsDevelopment())
            {
                if (!targeted)
                {
                    logger.LogDebug("Dev instance: ignoring message without the '{Prefix}' prefix.", prefix);
                    return;
                }
                message = message with { Text = body![prefix.Length..].TrimStart() };
            }
            else if (targeted)
            {
                logger.LogDebug("Prod instance: ignoring '{Prefix}'-targeted message; a dev instance will take it.", prefix);
                return;
            }
        }

        if (IsClearCommand(message))
        {
            await responder.ResetAsync(cancellationToken);
            await sender.SendAsync(replyTarget, "🧹 Cleared — starting a fresh conversation.", cancellationToken);
            return;
        }

        // Apple Voice Memos shared from iOS (.m4a / audio/mp4) → structured memo pipeline,
        // bypassing the agent. WhatsApp-native voice notes (.ogg/opus) go to the agent instead.
        if (message.Kind == InboundKind.Audio && IsSharedVoiceMemo(message))
        {
            try
            {
                var reply = await ProcessAsMemoAsync(message, cancellationToken);
                await sender.SendAsync(replyTarget, reply, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing Apple Voice Memo.");
                await sender.SendAsync(replyTarget, $"⚠️ Something went wrong: {ex.Message}", cancellationToken);
            }
            finally
            {
                CleanupMedia(message, o.MediaTempDir);
            }
            return;
        }

        try
        {
            var messages = await BuildMessagesAsync(message, cancellationToken);
            if (messages is null)
            {
                await sender.SendAsync(replyTarget, "Sorry — I can only handle text, voice notes, and images right now.", cancellationToken);
                return;
            }

            // Prepend the current local time so the agent can resolve relative schedules ("tomorrow 9am").
            var turn = new List<ChatMessage>(messages.Count + 1) { timeContext.Message() };
            turn.AddRange(messages);

            var sw = Stopwatch.StartNew();
            var reply = await responder.RespondAsync(turn, cancellationToken);
            sw.Stop();

            // Per-turn telemetry → Seq (carries app=Erda). Tokens + tools + latency.
            logger.LogInformation(
                "WhatsApp turn complete: type={Type} tokensIn={TokensIn} tokensOut={TokensOut} tokensTotal={TokensTotal} tools={Tools} replyChars={ReplyChars} ms={ElapsedMs}",
                message.Type, reply.InputTokens, reply.OutputTokens, reply.TotalTokens, reply.ToolsUsed, reply.Text.Length, sw.ElapsedMilliseconds);

            await sender.SendAsync(replyTarget, string.IsNullOrWhiteSpace(reply.Text) ? "(no response)" : reply.Text, cancellationToken);
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

    /// <summary>
    /// Apple Voice Memos shared from iOS arrive as .m4a (audio/mp4 or audio/x-m4a).
    /// WhatsApp-native voice notes use audio/ogg with Opus codec.
    /// </summary>
    private static bool IsSharedVoiceMemo(InboundMessage message)
    {
        var mime = message.MimeType?.Split(';')[0].Trim() ?? "";
        if (mime.Equals("audio/mp4", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("audio/x-m4a", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("audio/m4a", StringComparison.OrdinalIgnoreCase))
            return true;
        // Fall back to file extension if MIME is absent or unexpected.
        return Path.GetExtension(message.MediaPath ?? "").Equals(".m4a", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Transcribe the audio and run it through the memo pipeline (Codex → 1 Inbox/).</summary>
    private async Task<string> ProcessAsMemoAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.MediaPath) || !File.Exists(message.MediaPath))
            return "⚠️ Could not find the audio file.";
        var transcript = await transcriber.TranscribeAsync(message.MediaPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(transcript))
            return "⚠️ Transcription returned no text.";
        return await memoProcessor.ProcessAsync(transcript, cancellationToken);
    }

    /// <summary>True for a "clear"/"reset" command (optionally slash-prefixed) that wipes context.</summary>
    private static bool IsClearCommand(InboundMessage message) =>
        message.Kind == InboundKind.Text &&
        message.Text?.Trim().TrimStart('/') is { } t &&
        (t.Equals("clear", StringComparison.OrdinalIgnoreCase) || t.Equals("reset", StringComparison.OrdinalIgnoreCase));

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
