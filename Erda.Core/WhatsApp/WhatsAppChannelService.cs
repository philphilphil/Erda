using System.Diagnostics;
using Erda.Core.Abstractions;
using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Erda.Core.WhatsApp;

/// <summary>
/// Turns an inbound WhatsApp message into an agent turn and sends the reply back via the bridge.
/// Enforces the owner whitelist, dispatches by message type (text / voice / image), and cleans up
/// downloaded media afterwards.
///
/// Audio dispatch keys off the WhatsApp push-to-talk (PTT) flag: a voice note recorded in WhatsApp
/// (ptt=true) goes to the agent as a transcript for conversational handling, regardless of codec.
/// A shared Apple Voice Memo file (ptt=false, .m4a/audio-mp4) is routed directly to
/// <see cref="MemoProcessor"/> (structured memo → 1 Inbox/), bypassing the agent.
///
/// Either way the audio is archived (<see cref="IVoiceMemoArchive"/>) once the message has passed the
/// replay/owner/dev-prefix gates: an Apple memo as <see cref="VoiceMemoSource.AppleMemo"/> linking the
/// note it produced, a voice note as <see cref="VoiceMemoSource.WhatsAppVoice"/> keeping the transcript
/// (the turn produces no note). Audio that arrived via <c>/upload</c> is already archived by
/// <c>UploadIntake</c> and is never recorded a second time.
/// </summary>
public sealed class WhatsAppChannelService(
    IOptions<WhatsAppOptions> options,
    IAgentResponder responder,
    ITranscriber transcriber,
    IMemoProcessor memoProcessor,
    IWhatsAppSender sender,
    IVoiceMemoArchive voiceArchive,
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

        // Shared Apple Voice Memo files (.m4a / audio/mp4, NOT push-to-talk) → structured memo
        // pipeline, bypassing the agent. WhatsApp-recorded voice notes (ptt=true, any codec) go to
        // the agent for conversational handling — the ptt flag is the reliable discriminator, since
        // iOS PTT notes can arrive with an m4a/mp4 MIME that the format heuristic alone misreads.
        if (message.Kind == InboundKind.Audio && !message.Ptt && IsSharedVoiceMemo(message))
        {
            // An API upload already has an archive row (created by UploadIntake) — never record it twice.
            var memoArchiveId = message.VoiceArchiveId
                ?? await TryRecordAsync(message, VoiceMemoSource.AppleMemo, cancellationToken);
            var memoHandled = false;
            try
            {
                // ProcessAsMemoAsync saves a raw-transcript fallback if the reasoner fails, so a normal
                // return means the memo's content is safely in the vault and the audio can be deleted.
                var outcome = await ProcessAsMemoAsync(message, cancellationToken);
                await sender.SendAsync(replyTarget, outcome.Reply, cancellationToken);
                // Link the produced note (or terminal status) back to the archive row.
                if (memoArchiveId is { } archiveId)
                    await voiceArchive.CompleteAsync(archiveId, outcome.NotePath, outcome.Status, ct: cancellationToken);
                memoHandled = true;
            }
            catch (Exception ex)
            {
                // We couldn't even transcribe/save (not just a formatting failure) — keep the temp audio,
                // and say so. (The audio is also durably kept in the archive regardless.)
                logger.LogError(ex, "Error processing Apple Voice Memo; keeping audio {Path} for retry.", message.MediaPath);
                await sender.SendAsync(replyTarget,
                    $"⚠️ Couldn't process that voice memo: {ex.Message}. I kept the audio so it isn't lost.",
                    cancellationToken);
                if (memoArchiveId is { } archiveId)
                    await voiceArchive.FailAsync(archiveId, cancellationToken);
            }
            finally
            {
                if (memoHandled)
                    CleanupMedia(message, o.MediaTempDir);
            }
            return;
        }

        // Keep the "typing…" indicator alive while Erda generates. WhatsApp's composing presence
        // auto-expires (~25s) and a gpt-5.5 streamed run can take longer, so renew it on a cadence;
        // the finally cancels the loop and clears back to "paused".
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? typing = null;
        // Only delete downloaded media once the turn is safely handled. On an upstream model failure (or
        // an exception) we KEEP it, so a voice note/image isn't lost to a transient outage.
        var handled = false;
        // Audio handled conversationally (a WhatsApp voice note) is archived too, even though it produces
        // no note — recorded BEFORE the turn, while the downloaded file still exists. Text and images are
        // never archived.
        long? voiceArchiveId = null;
        try
        {
            if (message.Kind == InboundKind.Audio)
                voiceArchiveId = message.VoiceArchiveId
                    ?? await TryRecordAsync(message, VoiceMemoSource.WhatsAppVoice, cancellationToken);

            var input = await BuildMessagesAsync(message, cancellationToken);
            if (input is not { } turnInput)
            {
                // Unsupported type, unreadable media, or an empty transcript — nothing to retry.
                handled = true;
                if (voiceArchiveId is { } unreadableId)
                    await voiceArchive.FailAsync(unreadableId, cancellationToken);
                await sender.SendAsync(replyTarget, "Sorry — I can only handle text, voice notes, and images right now.", cancellationToken);
                return;
            }

            // Prepend the current local time so the agent can resolve relative schedules ("tomorrow 9am").
            var turn = new List<ChatMessage>(turnInput.Messages.Count + 1) { timeContext.Message() };
            turn.AddRange(turnInput.Messages);

            typing = KeepComposingAsync(replyTarget, typingCts.Token);

            var sw = Stopwatch.StartNew();
            var reply = await responder.RespondAsync(turn, cancellationToken);
            sw.Stop();

            // Per-turn telemetry → Seq (carries app=Erda). Tokens + tools + latency.
            logger.LogInformation(
                "WhatsApp turn complete: type={Type} tokensIn={TokensIn} tokensOut={TokensOut} tokensTotal={TokensTotal} tools={Tools} replyChars={ReplyChars} ms={ElapsedMs}",
                message.Type, reply.InputTokens, reply.OutputTokens, reply.TotalTokens, reply.ToolsUsed, reply.Text.Length, sw.ElapsedMilliseconds);

            // Make an upstream model failure loud instead of a silent "(no response)", and don't mark the
            // turn handled — so the media is kept for a retry.
            if (reply.IsUpstreamFailure)
            {
                logger.LogWarning(
                    "WhatsApp {Type} turn produced no response — upstream model failure (no text, no usage, no tools). Media kept: {Media}.",
                    message.Type, message.MediaPath ?? "(none)");
                await sender.SendAsync(replyTarget,
                    "⚠️ The model didn't return anything (it may be overloaded). Please try again in a moment.",
                    cancellationToken);
                if (voiceArchiveId is { } failedId)
                    await voiceArchive.FailAsync(failedId, cancellationToken);
            }
            else
            {
                handled = true;
                await sender.SendAsync(replyTarget, string.IsNullOrWhiteSpace(reply.Text) ? "(no response)" : reply.Text, cancellationToken);
                // No note is produced by a conversational turn, so the transcript IS the record.
                if (voiceArchiveId is { } answeredId)
                    await voiceArchive.CompleteAsync(answeredId, notePath: null, VoiceMemoStatus.Answered,
                        turnInput.Transcript, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling WhatsApp message of type {Type}.", message.Type);
            await sender.SendAsync(replyTarget, $"⚠️ Something went wrong: {ex.Message}", cancellationToken);
            if (voiceArchiveId is { } erroredId)
                await voiceArchive.FailAsync(erroredId, cancellationToken);
        }
        finally
        {
            // Stop renewing, then always clear the typing indicator, even on error/cancellation.
            typingCts.Cancel();
            if (typing is not null)
            {
                try { await typing; } catch { /* best-effort: presence cleanup never fails the turn */ }
            }
            // Use None, not cancellationToken: on cancellation the token is already tripped, so passing it
            // would abort the "paused" POST mid-flight and leave WhatsApp stuck showing "typing…".
            await sender.SetPresenceAsync(replyTarget, "paused", CancellationToken.None);
            if (handled)
                CleanupMedia(message, o.MediaTempDir);
            else if (!string.IsNullOrEmpty(message.MediaPath))
                logger.LogWarning("Kept undeleted media {Path} after an unhandled turn so it isn't lost.", message.MediaPath);
        }
    }

    /// <summary>
    /// Re-sends the "composing" (typing…) presence on a fixed cadence until cancelled — WhatsApp
    /// expires it after ~25s, so a long gpt-5.5 turn needs it refreshed. Best-effort: the sender
    /// swallows errors and cancellation ends the loop quietly.
    /// </summary>
    private async Task KeepComposingAsync(string target, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await sender.SetPresenceAsync(target, "composing", ct);
                await Task.Delay(TimeSpan.FromSeconds(12), ct);
            }
        }
        catch (OperationCanceledException) { /* turn finished */ }
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

    /// <summary>
    /// Archive WhatsApp-arriving audio (uploads are archived by <c>UploadIntake</c> before they get here).
    /// Returns null when there is no readable file, or when the archive itself failed — best-effort, an
    /// archiving problem must never fail the turn.
    /// </summary>
    private async Task<long?> TryRecordAsync(InboundMessage message, VoiceMemoSource source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.MediaPath) || !File.Exists(message.MediaPath))
            return null;
        return await voiceArchive.RecordAsync(ArchiveDisplayName(message, source), message.MediaPath, source, cancellationToken);
    }

    /// <summary>
    /// A display name for audio that arrived over WhatsApp: there is no original filename, so use a
    /// timestamped one that keeps the real extension (e.g. <c>whatsapp-voice-2026-07-29_1830.ogg</c>).
    /// </summary>
    private static string ArchiveDisplayName(InboundMessage message, VoiceMemoSource source)
    {
        var stem = source == VoiceMemoSource.AppleMemo ? "voice-memo" : "whatsapp-voice";
        return $"{stem}-{DateTimeOffset.Now:yyyy-MM-dd_HHmm}{Path.GetExtension(message.MediaPath ?? "")}";
    }

    /// <summary>The user-facing reply, the vault note it produced (if any), and a terminal archive status.</summary>
    private readonly record struct MemoOutcome(string Reply, string? NotePath, VoiceMemoStatus Status);

    /// <summary>Transcribe the audio and run it through the memo pipeline (Codex → 1 Inbox/).</summary>
    private async Task<MemoOutcome> ProcessAsMemoAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.MediaPath) || !File.Exists(message.MediaPath))
            return new MemoOutcome("⚠️ Could not find the audio file.", null, VoiceMemoStatus.Failed);
        var transcript = await transcriber.TranscribeAsync(message.MediaPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(transcript))
            return new MemoOutcome("⚠️ Transcription returned no text.", null, VoiceMemoStatus.Failed);
        try
        {
            var result = await memoProcessor.ProcessAsync(transcript, cancellationToken);
            return new MemoOutcome(result.Reply, result.NotePath, VoiceMemoStatus.Filed);
        }
        catch (Exception ex)
        {
            // The reasoner is down/overloaded (see ResponsesReasoner). We already have the transcript, so
            // don't lose the memo — save it raw to the inbox and tell the owner it wasn't formatted.
            var saved = await memoProcessor.SaveRawAsync(transcript, cancellationToken);
            logger.LogWarning(ex, "Memo formatting failed; saved raw transcript to {Path}.", saved);
            return new MemoOutcome(
                $"⚠️ Couldn't format that voice memo (model unavailable) — saved the raw transcript to {saved}.",
                saved, VoiceMemoStatus.Raw);
        }
    }

    /// <summary>True for a "clear"/"reset" command (optionally slash-prefixed) that wipes context.</summary>
    private static bool IsClearCommand(InboundMessage message) =>
        message.Kind == InboundKind.Text &&
        message.Text?.Trim().TrimStart('/') is { } t &&
        (t.Equals("clear", StringComparison.OrdinalIgnoreCase) || t.Equals("reset", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a turn feeds the agent: the chat message(s), plus the transcript they were built from for
    /// audio (null for text/images) so the archive can keep it — an answered voice note leaves no note.
    /// </summary>
    private readonly record struct TurnInput(IReadOnlyList<ChatMessage> Messages, string? Transcript);

    /// <summary>Builds the turn input for the agent, or null for an unsupported/empty message.</summary>
    private async Task<TurnInput?> BuildMessagesAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case InboundKind.Text:
            {
                var text = message.Text?.Trim();
                return string.IsNullOrEmpty(text)
                    ? null
                    : new TurnInput([new ChatMessage(ChatRole.User, text)], null);
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
                return new TurnInput([new ChatMessage(ChatRole.User, $"[Voice note transcript]\n{transcript}")], transcript);
            }

            case InboundKind.Image:
            {
                if (string.IsNullOrEmpty(message.MediaPath) || !File.Exists(message.MediaPath))
                    return null;
                var bytes = await File.ReadAllBytesAsync(message.MediaPath, cancellationToken);
                var mime = string.IsNullOrWhiteSpace(message.MimeType) ? "image/jpeg" : message.MimeType.Split(';')[0].Trim();
                var caption = string.IsNullOrWhiteSpace(message.Text) ? "Describe this image." : message.Text!.Trim();
                IList<AIContent> content = [new TextContent(caption), new DataContent(bytes, mime)];
                return new TurnInput([new ChatMessage(ChatRole.User, content)], null);
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
