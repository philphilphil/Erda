using Erda.Core.Configuration;
using Erda.Core.Services;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class WhatsAppChannelServiceTests
{
    private const string OwnerNumber = "+49 151 2345 6789";
    private const string OwnerJid = "4915123456789@s.whatsapp.net";

    private static WhatsAppChannelService Make(
        out FakeAgentResponder responder, out FakeWhatsAppSender sender, out FakeTranscriber transcriber,
        string environment = "Production", string devPrefix = "@dev")
        => MakeWith(new FakeMemoProcessor(), out responder, out sender, out transcriber, environment, devPrefix);

    private static WhatsAppChannelService MakeWith(
        FakeMemoProcessor memo, out FakeAgentResponder responder, out FakeWhatsAppSender sender,
        out FakeTranscriber transcriber, string environment = "Production", string devPrefix = "@dev",
        FakeVoiceMemoArchive? archive = null)
    {
        responder = new FakeAgentResponder();
        sender = new FakeWhatsAppSender();
        transcriber = new FakeTranscriber();
        archive ??= new FakeVoiceMemoArchive();
        var opts = Options.Create(new WhatsAppOptions
        {
            Enabled = true,
            OwnerNumber = OwnerNumber,
            MediaTempDir = Path.GetTempPath(),
            DevPrefix = devPrefix,
        });
        var timeContext = new CurrentTimeContext(new FakeClock(), Options.Create(new ReminderOptions()));
        var env = new FakeHostEnvironment { EnvironmentName = environment };
        return new WhatsAppChannelService(opts, responder, transcriber, memo, sender, archive, env, timeContext, NullLogger<WhatsAppChannelService>.Instance);
    }

    private static string TempMedia(string ext, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Text_from_owner_is_answered()
    {
        var svc = Make(out var responder, out var sender, out _);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "text", Text = "hi" });

        Assert.Single(responder.Calls);
        Assert.Single(sender.Sent);
        Assert.Equal(OwnerJid, sender.Sent[0].To);
        Assert.Equal("ok", sender.Sent[0].Text);
    }

    [Fact]
    public async Task Agent_turn_is_prefixed_with_current_time_context()
    {
        var svc = Make(out var responder, out _, out _);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "text", Text = "hi" });

        var messages = responder.Calls.Single();
        // User role, not System: the chat endpoint rejects system-role input items (see CurrentTimeContext).
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Contains("2026-06-15", messages[0].Text); // FakeClock's date
        Assert.Contains("hi", messages[^1].Text);         // the user's message still present
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("/clear")]
    [InlineData("Reset")]
    public async Task Clear_command_resets_without_calling_the_agent(string text)
    {
        var svc = Make(out var responder, out var sender, out _);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "text", Text = text });

        Assert.Empty(responder.Calls);
        Assert.Equal(1, responder.Resets);
        Assert.Single(sender.Sent);
        Assert.Contains("Cleared", sender.Sent[0].Text);
    }

    [Fact]
    public async Task Dev_instance_answers_only_prefixed_messages_and_strips_the_prefix()
    {
        var svc = Make(out var responder, out var sender, out _, environment: Environments.Development);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "text", Text = "@dev hi there" });

        Assert.Single(responder.Calls);
        Assert.Single(sender.Sent);
        Assert.Equal("hi there", responder.Calls[0][^1].Text); // prefix stripped before the agent sees it
    }

    [Fact]
    public async Task Dev_instance_ignores_unprefixed_messages()
    {
        var svc = Make(out var responder, out var sender, out _, environment: Environments.Development);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "text", Text = "hi there" });

        Assert.Empty(responder.Calls);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Prod_instance_ignores_prefixed_messages()
    {
        var svc = Make(out var responder, out var sender, out _, environment: Environments.Production);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "text", Text = "@dev hi there" });

        Assert.Empty(responder.Calls);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Empty_dev_prefix_disables_gating_so_dev_answers_everything()
    {
        var svc = Make(out var responder, out _, out _, environment: Environments.Development, devPrefix: "");
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "text", Text = "hi there" });

        Assert.Single(responder.Calls);
    }

    [Fact]
    public async Task Message_from_non_owner_is_dropped()
    {
        var svc = Make(out var responder, out var sender, out _);
        await svc.ProcessAsync(new InboundMessage { From = "4999999@s.whatsapp.net", Chat = "4999999@s.whatsapp.net", Type = "text", Text = "hi" });

        Assert.Empty(responder.Calls);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Group_message_is_dropped()
    {
        var svc = Make(out var responder, out var sender, out _);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = "12345@g.us", Type = "text", Text = "hi" });

        Assert.Empty(responder.Calls);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Voice_note_is_transcribed_then_answered_and_media_cleaned()
    {
        var svc = Make(out var responder, out var sender, out var transcriber);
        transcriber.Transcript = "buy milk";
        var media = TempMedia(".ogg", [1, 2, 3]);

        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "audio", MediaPath = media, MimeType = "audio/ogg" });

        Assert.Equal(1, transcriber.Calls);
        Assert.Single(responder.Calls);
        Assert.Contains(responder.Calls[0][^1].Contents.OfType<TextContent>(), t => t.Text.Contains("buy milk"));
        Assert.Single(sender.Sent);
        Assert.False(File.Exists(media)); // cleaned up (inside MediaTempDir)
    }

    [Fact]
    public async Task Ptt_voice_note_goes_to_the_agent_even_when_mime_is_m4a()
    {
        // A WhatsApp-recorded voice note (ptt=true) must be conversational, NOT filed to the inbox,
        // even if it arrives with an m4a/mp4 MIME (iOS sometimes encodes PTT as AAC/mp4).
        var memo = new FakeMemoProcessor();
        var svc = MakeWith(memo, out var responder, out var sender, out var transcriber);
        transcriber.Transcript = "what's the weather tomorrow";
        var media = TempMedia(".m4a", [1, 2, 3]);

        await svc.ProcessAsync(new InboundMessage
        {
            From = OwnerJid, Chat = OwnerJid, Type = "audio",
            MediaPath = media, MimeType = "audio/mp4", Ptt = true,
        });

        Assert.Empty(memo.Transcripts);   // NOT routed to the inbox memo pipeline
        Assert.Single(responder.Calls);   // handled conversationally by the agent
        Assert.Contains(responder.Calls[0][^1].Contents.OfType<TextContent>(), t => t.Text.Contains("what's the weather"));
        Assert.False(File.Exists(media)); // cleaned up
    }

    [Fact]
    public async Task Upstream_model_failure_is_surfaced_and_media_is_kept()
    {
        // An empty reply with no usage/tools = the Responses backend failed (overloaded). The owner must
        // be told (not a silent "(no response)"), and the voice note must NOT be deleted, so it survives.
        var svc = Make(out var responder, out var sender, out var transcriber);
        transcriber.Transcript = "what's the weather tomorrow";
        responder.Reply = new Erda.Core.Abstractions.AgentReply("", null, null, null, []);
        var media = TempMedia(".ogg", [1, 2, 3]);

        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "audio", MediaPath = media, MimeType = "audio/ogg", Ptt = true });

        Assert.Single(sender.Sent);
        Assert.Contains("overloaded", sender.Sent[0].Text);
        Assert.NotEqual("(no response)", sender.Sent[0].Text);
        Assert.True(File.Exists(media)); // kept so the memo isn't lost
        File.Delete(media);
    }

    [Fact]
    public async Task Memo_reasoner_failure_saves_raw_transcript_and_cleans_media()
    {
        // If the reasoner is unavailable, the transcript must not be lost: fall back to a raw save, and
        // since the content is now safely in the vault the audio can be cleaned up.
        var memo = new FakeMemoProcessor { ThrowOnProcess = true };
        var svc = MakeWith(memo, out _, out var sender, out var transcriber);
        transcriber.Transcript = "remember to call mom";
        var media = TempMedia(".m4a", [1, 2, 3]);

        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "audio", MediaPath = media, MimeType = "audio/mp4", Ptt = false });

        Assert.Single(memo.Transcripts);           // formatting was attempted
        Assert.Single(memo.RawTranscripts);         // …and fell back to a raw save
        Assert.Equal("remember to call mom", memo.RawTranscripts[0]);
        Assert.Contains("raw transcript", sender.Sent[0].Text);
        Assert.False(File.Exists(media));           // content is safe in the vault → audio cleaned
    }

    [Fact]
    public async Task Upload_memo_links_produced_note_to_the_archive_row()
    {
        // An API upload carries a VoiceArchiveId; once filed, the channel links the note path + status
        // back to that archive row so the panel can show which note the memo produced.
        var memo = new FakeMemoProcessor { NotePath = "1 Inbox/2026_groceries.md" };
        var archive = new FakeVoiceMemoArchive();
        var svc = MakeWith(memo, out _, out _, out var transcriber, archive: archive);
        transcriber.Transcript = "buy groceries";
        var media = TempMedia(".m4a", [1, 2, 3]);

        await svc.ProcessAsync(new InboundMessage
        {
            From = OwnerJid, Chat = OwnerJid, Type = "audio",
            MediaPath = media, MimeType = "audio/mp4", Ptt = false, VoiceArchiveId = 42,
        });

        var (id, notePath, status) = Assert.Single(archive.Completed);
        Assert.Equal(42, id);
        Assert.Equal("1 Inbox/2026_groceries.md", notePath);
        Assert.Equal("filed", status);
    }

    [Fact]
    public async Task Non_ptt_m4a_file_still_goes_to_the_inbox_memo_pipeline()
    {
        // A shared Apple Voice Memo (a non-PTT .m4a file) keeps going to the structured inbox pipeline.
        var memo = new FakeMemoProcessor();
        var svc = MakeWith(memo, out var responder, out var sender, out var transcriber);
        transcriber.Transcript = "remember to call mom";
        var media = TempMedia(".m4a", [1, 2, 3]);

        await svc.ProcessAsync(new InboundMessage
        {
            From = OwnerJid, Chat = OwnerJid, Type = "audio",
            MediaPath = media, MimeType = "audio/mp4", Ptt = false,
        });

        Assert.Single(memo.Transcripts);  // shared Apple Voice Memo → inbox
        Assert.Empty(responder.Calls);    // not the conversational agent
    }

    [Fact]
    public async Task Image_is_sent_as_text_plus_image_content()
    {
        var svc = Make(out var responder, out var sender, out _);
        var media = TempMedia(".jpg", [255, 216, 255, 224]);

        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "image", Text = "what is this?", MediaPath = media, MimeType = "image/jpeg" });

        Assert.Single(responder.Calls);
        var contents = responder.Calls[0][^1].Contents;
        Assert.Contains(contents.OfType<TextContent>(), t => t.Text.Contains("what is this?"));
        Assert.Contains(contents, c => c is DataContent);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Unsupported_type_gets_a_polite_reply()
    {
        var svc = Make(out var responder, out var sender, out _);
        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "sticker" });

        Assert.Empty(responder.Calls);
        Assert.Single(sender.Sent);
        Assert.Contains("only handle", sender.Sent[0].Text);
    }
}
