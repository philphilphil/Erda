using Erda.Core.Configuration;
using Erda.Core.Services;
using Erda.Core.Upload;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class UploadIntakeTests
{
    private const string OwnerNumber = "+49 151 2345 6789";
    private const string OwnerJid = "4915123456789@s.whatsapp.net";
    private const string ApiKey = "super-secret-key";

    private static UploadIntake Make(out WhatsAppInboundQueue queue, string mediaDir, string apiKey = ApiKey, int maxMb = 50)
    {
        queue = new WhatsAppInboundQueue();
        var upload = Options.Create(new UploadOptions { Enabled = true, ApiKey = apiKey, MaxUploadMb = maxMb });
        var whatsApp = Options.Create(new WhatsAppOptions { Enabled = true, OwnerNumber = OwnerNumber, MediaTempDir = mediaDir });
        return new UploadIntake(upload, whatsApp, queue, new FakeVoiceMemoArchive(), NullLogger<UploadIntake>.Instance);
    }

    [Theory]
    [InlineData("Bearer super-secret-key", true)]   // exact match
    [InlineData("Bearer wrong-key", false)]          // wrong token
    [InlineData("Bearer ", false)]                   // empty token
    [InlineData("super-secret-key", false)]          // missing scheme
    [InlineData("bearer super-secret-key", false)]   // wrong-case scheme
    [InlineData("", false)]                          // empty header
    [InlineData(null, false)]                        // no header
    public void IsAuthorized_only_accepts_an_exact_bearer_match(string? header, bool expected)
    {
        var intake = Make(out _, Path.GetTempPath());
        Assert.Equal(expected, intake.IsAuthorized(header));
    }

    [Fact]
    public void IsAuthorized_rejects_everything_when_the_key_is_unset()
    {
        var intake = Make(out _, Path.GetTempPath(), apiKey: "");
        Assert.False(intake.IsAuthorized("Bearer "));
        Assert.False(intake.IsAuthorized("Bearer anything"));
    }

    [Fact]
    public async Task IngestAsync_rejects_an_empty_body_as_NoFile()
    {
        var intake = Make(out _, Path.GetTempPath());
        var outcome = await intake.IngestAsync(0, new MemoryStream());
        Assert.Equal(UploadOutcome.NoFile, outcome);
    }

    [Fact]
    public async Task IngestAsync_rejects_an_oversize_body_as_TooLarge()
    {
        var intake = Make(out var queue, Path.GetTempPath(), maxMb: 1);
        var outcome = await intake.IngestAsync(2L * 1024 * 1024, new MemoryStream([1, 2, 3]));
        Assert.Equal(UploadOutcome.TooLarge, outcome);
        Assert.False(await HasQueued(queue)); // nothing enqueued
    }

    [Fact]
    public async Task IngestAsync_accepts_a_raw_body_of_unknown_length()
    {
        var mediaDir = Path.Combine(Path.GetTempPath(), "erda-upload-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mediaDir);
        try
        {
            var intake = Make(out var queue, mediaDir);
            var bytes = new byte[] { 9, 8, 7 };

            // declaredLength null mimics a raw body with no Content-Length.
            var outcome = await intake.IngestAsync(null, new MemoryStream(bytes));

            Assert.Equal(UploadOutcome.Accepted, outcome);
            var msg = await Dequeue(queue);
            Assert.Equal("audio/mp4", msg.MimeType);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(msg.MediaPath!));
        }
        finally
        {
            if (Directory.Exists(mediaDir))
                Directory.Delete(mediaDir, recursive: true);
        }
    }

    [Fact]
    public async Task IngestAsync_enforces_the_cap_against_bytes_written_when_length_is_unknown()
    {
        var mediaDir = Path.Combine(Path.GetTempPath(), "erda-upload-rawbig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mediaDir);
        try
        {
            var intake = Make(out var queue, mediaDir, maxMb: 1);

            // No declared length, but the actual bytes exceed the 1 MB cap — caught after the save.
            var outcome = await intake.IngestAsync(null, new MemoryStream(new byte[2 * 1024 * 1024]));

            Assert.Equal(UploadOutcome.TooLarge, outcome);
            Assert.False(await HasQueued(queue));
            Assert.Empty(Directory.GetFiles(mediaDir)); // the oversize file was cleaned up
        }
        finally
        {
            if (Directory.Exists(mediaDir))
                Directory.Delete(mediaDir, recursive: true);
        }
    }

    [Fact]
    public async Task IngestAsync_treats_an_unknown_length_empty_body_as_NoFile()
    {
        var intake = Make(out var queue, Path.GetTempPath());
        var outcome = await intake.IngestAsync(null, new MemoryStream());
        Assert.Equal(UploadOutcome.NoFile, outcome);
        Assert.False(await HasQueued(queue));
    }

    [Fact]
    public async Task IngestAsync_saves_the_audio_and_enqueues_a_voice_memo_addressed_to_the_owner()
    {
        var mediaDir = Path.Combine(Path.GetTempPath(), "erda-upload-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var intake = Make(out var queue, mediaDir);
            var bytes = new byte[] { 1, 2, 3, 4 };

            var outcome = await intake.IngestAsync(bytes.Length, new MemoryStream(bytes));

            Assert.Equal(UploadOutcome.Accepted, outcome);

            var msg = await Dequeue(queue);
            Assert.Equal(OwnerJid, msg.From);
            Assert.Equal(OwnerJid, msg.Chat);
            Assert.Equal("audio", msg.Type);
            Assert.Equal(InboundKind.Audio, msg.Kind);
            Assert.Equal("audio/mp4", msg.MimeType);
            Assert.Equal(0, msg.Timestamp); // one-shot upload opts out of the replay-drop guard

            Assert.NotNull(msg.MediaPath);
            Assert.StartsWith(Path.GetFullPath(mediaDir), Path.GetFullPath(msg.MediaPath!));
            Assert.EndsWith(".m4a", msg.MediaPath!); // extension the IsSharedVoiceMemo fallback + transcription rely on
            Assert.True(File.Exists(msg.MediaPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(msg.MediaPath!));
        }
        finally
        {
            if (Directory.Exists(mediaDir))
                Directory.Delete(mediaDir, recursive: true);
        }
    }

    [Fact]
    public async Task An_uploaded_memo_survives_the_replay_guard_and_runs_the_voice_memo_pipeline()
    {
        var mediaDir = Path.Combine(Path.GetTempPath(), "erda-upload-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mediaDir);
        try
        {
            // The same options object backs both the intake and the channel service, so the saved file
            // lands where the channel service's CleanupMedia expects it.
            var whatsApp = Options.Create(new WhatsAppOptions { Enabled = true, OwnerNumber = OwnerNumber, MediaTempDir = mediaDir });
            var queue = new WhatsAppInboundQueue();
            var upload = Options.Create(new UploadOptions { Enabled = true, ApiKey = ApiKey, MaxUploadMb = 50 });
            var archive = new FakeVoiceMemoArchive();
            var intake = new UploadIntake(upload, whatsApp, queue, archive, NullLogger<UploadIntake>.Instance);

            await intake.IngestAsync(4, new MemoryStream([1, 2, 3, 4]));
            var msg = await Dequeue(queue);

            // Drive the dequeued message through the SAME channel service the inbound worker uses, to
            // prove the synthesized audio/mp4 message is NOT replay-dropped (Timestamp 0) and routes
            // through IsSharedVoiceMemo → memo pipeline → WhatsApp reply.
            var memo = new FakeMemoProcessor();
            var sender = new FakeWhatsAppSender();
            var transcriber = new FakeTranscriber();
            var timeContext = new CurrentTimeContext(new FakeClock(), Options.Create(new ReminderOptions()));
            var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };
            var channel = new WhatsAppChannelService(
                whatsApp, new FakeAgentResponder(), transcriber, memo, sender, archive, env, timeContext,
                NullLogger<WhatsAppChannelService>.Instance);

            await channel.ProcessAsync(msg);

            Assert.Equal(1, transcriber.Calls);     // transcribed once
            Assert.Single(memo.Transcripts);         // ran the memo pipeline (→ 1 Inbox/)
            Assert.Single(sender.Sent);              // replied over WhatsApp
            Assert.Equal(OwnerJid, sender.Sent[0].To);
            Assert.False(File.Exists(msg.MediaPath!)); // media cleaned up (under MediaTempDir)
        }
        finally
        {
            if (Directory.Exists(mediaDir))
                Directory.Delete(mediaDir, recursive: true);
        }
    }

    private static async Task<InboundMessage> Dequeue(WhatsAppInboundQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var m in queue.ReadAllAsync(cts.Token))
            return m;
        throw new InvalidOperationException("queue was empty");
    }

    private static async Task<bool> HasQueued(WhatsAppInboundQueue queue)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (var _ in queue.ReadAllAsync(cts.Token))
                return true;
        }
        catch (OperationCanceledException) { }
        return false;
    }
}

public class UploadOptionsValidatorTests
{
    private static readonly UploadOptionsValidator Validator = new();

    [Fact]
    public void Disabled_passes_with_no_settings()
    {
        Assert.True(Validator.Validate(null, new UploadOptions { Enabled = false }).Succeeded);
    }

    [Fact]
    public void Enabled_with_key_and_positive_cap_passes()
    {
        Assert.True(Validator.Validate(null, new UploadOptions { Enabled = true, ApiKey = "k", MaxUploadMb = 50 }).Succeeded);
    }

    [Fact]
    public void Enabled_without_a_key_fails_naming_the_env_var()
    {
        var result = Validator.Validate(null, new UploadOptions { Enabled = true, ApiKey = "", MaxUploadMb = 50 });
        Assert.True(result.Failed);
        Assert.Contains("Upload__ApiKey", result.FailureMessage);
    }

    [Fact]
    public void Enabled_with_a_nonpositive_cap_fails()
    {
        var result = Validator.Validate(null, new UploadOptions { Enabled = true, ApiKey = "k", MaxUploadMb = 0 });
        Assert.True(result.Failed);
        Assert.Contains("Upload__MaxUploadMb", result.FailureMessage);
    }
}
