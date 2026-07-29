using Erda.Core.Configuration;
using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services;

/// <summary>
/// A row for the panel's voice-memo archive plus whether its audio file is still on disk.
/// <paramref name="Source"/> and <paramref name="Status"/> are the enums' lowercase-kebab wire
/// spelling (<c>apple-memo</c>, <c>answered</c>, …), mapped explicitly so the panel JSON never
/// depends on how System.Text.Json would serialize the enums.
/// </summary>
public sealed record VoiceMemoView(
    long Id,
    DateTimeOffset CreatedAtUtc,
    string FileName,
    string Source,
    string? NotePath,
    string Status,
    string? Transcript,
    long AudioBytes,
    bool HasAudio);

/// <summary>An opened archive audio file, ready to stream back to the browser's &lt;audio&gt; element.</summary>
public sealed record VoiceMemoAudio(Stream Content, string ContentType, string FileName);

/// <summary>
/// Durable archive of every piece of inbound voice audio — HTTP <c>/upload</c> memos, Apple Voice Memos
/// shared through WhatsApp, and WhatsApp-recorded voice notes (see <see cref="VoiceMemoSource"/>). Stores
/// each memo's audio in a dedicated directory next to the DB (so the media-temp cleanup never touches it)
/// and a row in <see cref="ErdaDbContext.VoiceMemos"/> linking date + filename + source + what it produced
/// (a note, or a transcript for an agent turn).
/// </summary>
public interface IVoiceMemoArchive
{
    /// <summary>
    /// Copy the just-saved audio into the archive and create a <c>pending</c> row. Returns the row
    /// id to thread through the pipeline (so <see cref="CompleteAsync"/>/<see cref="FailAsync"/> can link
    /// the note), or null if archiving failed (best-effort — a failure here must not block the memo).
    /// </summary>
    Task<long?> RecordAsync(string displayFileName, string sourceAudioPath, VoiceMemoSource source, CancellationToken ct = default);

    /// <summary>
    /// Mark a row processed, linking the produced note and a terminal status (<c>filed</c>/<c>raw</c>), or
    /// — for an <c>answered</c> agent turn, which produces no note — the transcript.
    /// </summary>
    Task CompleteAsync(long id, string? notePath, VoiceMemoStatus status, string? transcript = null, CancellationToken ct = default);

    /// <summary>Mark a row <c>failed</c> (transcription/processing could not produce anything).</summary>
    Task FailAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Sweep leftover <c>pending</c> rows to <c>failed</c> and return how many were touched. The inbound
    /// queue is in-memory, so a row still <c>pending</c> at startup belongs to a run that ended before the
    /// pipeline finished and can never be processed — it would otherwise show as pending forever. The
    /// audio is still in the archive and playable; only the note link is lost.
    /// </summary>
    Task<int> ReconcileStalePendingAsync(CancellationToken ct = default);

    /// <summary>All archived memos, newest first.</summary>
    Task<IReadOnlyList<VoiceMemoView>> ListAsync(CancellationToken ct = default);

    /// <summary>Open a row's archived audio for streaming, or null if the row/file is gone.</summary>
    Task<VoiceMemoAudio?> OpenAudioAsync(long id, CancellationToken ct = default);

    /// <summary>Delete a row and its audio file. The linked Obsidian note is intentionally left in place.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class VoiceMemoArchive(
    IDbContextFactory<ErdaDbContext> dbFactory,
    IOptions<ErdaOptions> options,
    ILogger<VoiceMemoArchive> logger) : IVoiceMemoArchive
{
    // Live beside the SQLite DB (the durable erda-data volume), NOT in the media temp dir which the
    // channel wipes after each turn.
    private string ArchiveDir
    {
        get
        {
            var dbDir = Path.GetDirectoryName(options.Value.DbPath);
            var root = string.IsNullOrEmpty(dbDir) ? "." : dbDir;
            return Path.Combine(root, "voice-archive");
        }
    }

    /// <inheritdoc />
    public async Task<long?> RecordAsync(string displayFileName, string sourceAudioPath, VoiceMemoSource source, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(ArchiveDir);
            var ext = Path.GetExtension(sourceAudioPath);
            var storedName = $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}";
            var dest = Path.Combine(ArchiveDir, storedName);
            File.Copy(sourceAudioPath, dest, overwrite: false);
            var bytes = new FileInfo(dest).Length;

            var row = new VoiceMemoRow
            {
                CreatedAtUtc = DateTimeOffset.UtcNow,
                FileName = string.IsNullOrWhiteSpace(displayFileName) ? storedName : displayFileName,
                Source = source,
                AudioFileName = storedName,
                AudioBytes = bytes,
                Status = VoiceMemoStatus.Pending,
            };
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.VoiceMemos.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not archive {Source} voice memo {File}; continuing without an archive row.", source, displayFileName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(long id, string? notePath, VoiceMemoStatus status, string? transcript = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.VoiceMemos.FindAsync([id], ct);
            if (row is null) return;
            row.NotePath = notePath;
            row.Transcript = transcript;
            row.Status = status;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not finalize voice-memo archive row {Id}.", id);
        }
    }

    /// <inheritdoc />
    public Task FailAsync(long id, CancellationToken ct = default) => CompleteAsync(id, null, VoiceMemoStatus.Failed, ct: ct);

    /// <inheritdoc />
    public async Task<int> ReconcileStalePendingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var stale = await db.VoiceMemos.Where(v => v.Status == VoiceMemoStatus.Pending).ToListAsync(ct);
            if (stale.Count == 0) return 0;

            foreach (var row in stale)
                row.Status = VoiceMemoStatus.Failed;
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Marked {Count} stale pending voice-memo archive row(s) as failed; they were left over from a previous process and can never be processed.",
                stale.Count);
            return stale.Count;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reconcile stale pending voice-memo archive rows.");
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VoiceMemoView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.VoiceMemos.OrderByDescending(v => v.Id).ToListAsync(ct);
        var dir = ArchiveDir;
        return rows.Select(r => new VoiceMemoView(
            r.Id, r.CreatedAtUtc, r.FileName, r.Source.ToWire(), r.NotePath, r.Status.ToWire(), r.Transcript, r.AudioBytes,
            HasAudio: r.AudioFileName.Length > 0 && File.Exists(Path.Combine(dir, r.AudioFileName)))).ToList();
    }

    /// <inheritdoc />
    public async Task<VoiceMemoAudio?> OpenAudioAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.VoiceMemos.FindAsync([id], ct);
        if (row is null || row.AudioFileName.Length == 0) return null;
        var path = Path.Combine(ArchiveDir, row.AudioFileName);
        if (!File.Exists(path)) return null;
        Stream stream = File.OpenRead(path);
        return new VoiceMemoAudio(stream, ContentTypeFor(row.AudioFileName), row.FileName);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.VoiceMemos.FindAsync([id], ct);
        if (row is null) return false;

        if (row.AudioFileName.Length > 0)
        {
            try
            {
                var path = Path.Combine(ArchiveDir, row.AudioFileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete archived audio for voice memo {Id}; removing the row anyway.", id);
            }
        }

        db.VoiceMemos.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".m4a" or ".mp4" => "audio/mp4",
        ".ogg" or ".opus" => "audio/ogg",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };
}
