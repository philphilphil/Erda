using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Tests for <see cref="VoiceMemoArchive"/> — the durable record of inbound voice audio: rows start
/// <c>pending</c>, the audio is copied next to the DB, the source/status enums round-trip through their
/// lowercase-kebab TEXT columns, and stale <c>pending</c> rows left by a previous process are swept to
/// <c>failed</c> at startup.
/// </summary>
public class VoiceMemoArchiveTests
{
    /// <summary>An archive rooted at a throwaway directory (the archive dir is derived from DbPath).</summary>
    private static VoiceMemoArchive Make(string root)
    {
        var options = Options.Create(new ErdaOptions { DbPath = Path.Combine(root, "erda.db") });
        return new VoiceMemoArchive(TestDb.NewFactory(), options, NullLogger<VoiceMemoArchive>.Instance);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "erda-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Writes a fake upload to disk and archives it; returns the new row id.</summary>
    private static async Task<long> RecordAsync(VoiceMemoArchive archive, string root, string displayName = "memo.m4a",
        VoiceMemoSource source = VoiceMemoSource.Upload)
    {
        var path = Path.Combine(root, "incoming-" + Guid.NewGuid().ToString("N") + ".m4a");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var id = await archive.RecordAsync(displayName, path, source);
        Assert.NotNull(id);
        return id!.Value;
    }

    [Fact]
    public async Task RecordAsync_creates_a_pending_row_and_copies_the_audio()
    {
        var root = NewRoot();
        try
        {
            var archive = Make(root);

            var id = await RecordAsync(archive, root, "Voice Memo.m4a");

            var row = Assert.Single(await archive.ListAsync());
            Assert.Equal(id, row.Id);
            Assert.Equal("Voice Memo.m4a", row.FileName);
            Assert.Equal("upload", row.Source);
            Assert.Equal("pending", row.Status);
            Assert.Null(row.NotePath);
            Assert.Null(row.Transcript);
            Assert.Equal(4, row.AudioBytes);
            Assert.True(row.HasAudio);

            // Copied into the archive dir beside the DB, not left only in the media temp dir.
            var stored = Assert.Single(Directory.GetFiles(Path.Combine(root, "voice-archive")));
            Assert.EndsWith(".m4a", stored);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcileStalePendingAsync_fails_pending_rows_and_leaves_terminal_ones_alone()
    {
        var root = NewRoot();
        try
        {
            var archive = Make(root);
            var stale1 = await RecordAsync(archive, root);
            var stale2 = await RecordAsync(archive, root);
            var filed = await RecordAsync(archive, root);
            var raw = await RecordAsync(archive, root);
            await archive.CompleteAsync(filed, "1 Inbox/memo.md", VoiceMemoStatus.Filed);
            await archive.CompleteAsync(raw, null, VoiceMemoStatus.Raw);

            var swept = await archive.ReconcileStalePendingAsync();

            Assert.Equal(2, swept);
            var rows = (await archive.ListAsync()).ToDictionary(r => r.Id);
            Assert.Equal("failed", rows[stale1].Status);
            Assert.Equal("failed", rows[stale2].Status);
            Assert.Equal("filed", rows[filed].Status);
            Assert.Equal("1 Inbox/memo.md", rows[filed].NotePath);   // the note link survives the sweep
            Assert.Equal("raw", rows[raw].Status);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcileStalePendingAsync_reports_nothing_when_there_is_nothing_to_sweep()
    {
        var root = NewRoot();
        try
        {
            var archive = Make(root);
            var id = await RecordAsync(archive, root);
            await archive.CompleteAsync(id, "1 Inbox/memo.md", VoiceMemoStatus.Filed);

            Assert.Equal(0, await archive.ReconcileStalePendingAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_answered_whatsapp_voice_row_round_trips_its_source_status_and_transcript()
    {
        // The enums persist as lowercase-kebab TEXT and the view re-exposes exactly those strings, so
        // the panel JSON never sees an integer and the values written before the enums still fit.
        var root = NewRoot();
        try
        {
            var archive = Make(root);
            var id = await RecordAsync(archive, root, "whatsapp-voice-2026-07-29_1830.ogg", VoiceMemoSource.WhatsAppVoice);

            await archive.CompleteAsync(id, null, VoiceMemoStatus.Answered, "what's the weather tomorrow");

            var row = Assert.Single(await archive.ListAsync());
            Assert.Equal("whatsapp-voice", row.Source);
            Assert.Equal("answered", row.Status);
            Assert.Equal("what's the weather tomorrow", row.Transcript);
            Assert.Null(row.NotePath);
            // A terminal row is not swept, and the transcript survives the sweep pass.
            Assert.Equal(0, await archive.ReconcileStalePendingAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_apple_memo_row_keeps_its_source_alongside_the_note_it_produced()
    {
        var root = NewRoot();
        try
        {
            var archive = Make(root);
            var id = await RecordAsync(archive, root, "voice-memo-2026-07-29_1830.m4a", VoiceMemoSource.AppleMemo);

            await archive.CompleteAsync(id, "1 Inbox/memo.md", VoiceMemoStatus.Filed);

            var row = Assert.Single(await archive.ListAsync());
            Assert.Equal("apple-memo", row.Source);
            Assert.Equal("filed", row.Status);
            Assert.Equal("1 Inbox/memo.md", row.NotePath);
            Assert.Null(row.Transcript);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_removes_the_row_and_its_audio()
    {
        var root = NewRoot();
        try
        {
            var archive = Make(root);
            var id = await RecordAsync(archive, root);
            var archiveDir = Path.Combine(root, "voice-archive");
            Assert.Single(Directory.GetFiles(archiveDir));

            Assert.True(await archive.DeleteAsync(id));

            Assert.Empty(await archive.ListAsync());
            Assert.Empty(Directory.GetFiles(archiveDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_returns_false_for_an_unknown_id()
    {
        var root = NewRoot();
        try
        {
            Assert.False(await Make(root).DeleteAsync(4711));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
