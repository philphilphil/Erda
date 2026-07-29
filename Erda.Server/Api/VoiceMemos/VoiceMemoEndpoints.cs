using Erda.Core.Services;

namespace Erda.Server.Api;

/// <summary>
/// JSON + audio endpoints over <see cref="IVoiceMemoArchive"/> for the panel's Voice-memo archive: every
/// piece of inbound voice audio — <c>POST /upload</c> memos, Apple Voice Memos shared through WhatsApp,
/// and WhatsApp voice notes. List rows, stream a row's audio for playback, and delete a row (audio +
/// entry; the Obsidian note is left in place).
/// </summary>
public static class VoiceMemoEndpoints
{
    public static RouteGroupBuilder MapVoiceMemoEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/voice-memos");

        g.MapGet("", async (IVoiceMemoArchive archive, CancellationToken ct) =>
            Results.Ok(await archive.ListAsync(ct)));

        // Range processing is enabled so the browser's <audio> element can seek/scrub.
        g.MapGet("/{id:long}/audio", async (long id, IVoiceMemoArchive archive, CancellationToken ct) =>
        {
            var audio = await archive.OpenAudioAsync(id, ct);
            return audio is null
                ? Results.NotFound()
                : Results.File(audio.Content, audio.ContentType, enableRangeProcessing: true);
        });

        g.MapDelete("/{id:long}", async (long id, IVoiceMemoArchive archive, CancellationToken ct) =>
            await archive.DeleteAsync(id, ct) ? Results.Ok() : Results.NotFound());

        return group;
    }
}
