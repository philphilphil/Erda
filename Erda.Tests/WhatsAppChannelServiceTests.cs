using Erda.Configuration;
using Erda.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class WhatsAppChannelServiceTests
{
    private const string OwnerNumber = "+49 151 2345 6789";
    private const string OwnerJid = "4915123456789@s.whatsapp.net";

    private static WhatsAppChannelService Make(
        out FakeAgentResponder responder, out FakeWhatsAppSender sender, out FakeTranscriber transcriber)
    {
        responder = new FakeAgentResponder();
        sender = new FakeWhatsAppSender();
        transcriber = new FakeTranscriber();
        var opts = Options.Create(new WhatsAppOptions
        {
            Enabled = true,
            OwnerNumber = OwnerNumber,
            MediaTempDir = Path.GetTempPath(),
        });
        return new WhatsAppChannelService(opts, responder, transcriber, sender, NullLogger<WhatsAppChannelService>.Instance);
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
        Assert.Contains(responder.Calls[0][0].Contents.OfType<TextContent>(), t => t.Text.Contains("buy milk"));
        Assert.Single(sender.Sent);
        Assert.False(File.Exists(media)); // cleaned up (inside MediaTempDir)
    }

    [Fact]
    public async Task Image_is_sent_as_text_plus_image_content()
    {
        var svc = Make(out var responder, out var sender, out _);
        var media = TempMedia(".jpg", [255, 216, 255, 224]);

        await svc.ProcessAsync(new InboundMessage { From = OwnerJid, Chat = OwnerJid, Type = "image", Text = "what is this?", MediaPath = media, MimeType = "image/jpeg" });

        Assert.Single(responder.Calls);
        var contents = responder.Calls[0][0].Contents;
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
