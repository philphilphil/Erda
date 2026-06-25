using System.Text.Json;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class NotifyToolsTests
{
    private sealed class FakeSender : IWhatsAppSender
    {
        public (string Jid, string Path, string? Caption)? ImageCall { get; private set; }
        public Task<bool> SendAsync(string toJid, string text, CancellationToken ct = default) => Task.FromResult(true);
        public Task SetPresenceAsync(string chatJid, string state, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SendImageAsync(string toJid, string filePath, string? caption, CancellationToken ct = default)
        {
            ImageCall = (toJid, filePath, caption);
            return Task.FromResult(true);
        }
    }

    private static NotifyTools Make(FakeSender sender) =>
        new(sender, Options.Create(new WhatsAppOptions { OwnerNumber = "+4915123456789" }));

    private static AIFunction Tool(NotifyTools tools, string name) =>
        (AIFunction)tools.AsTools().Single(t => ((AIFunction)t).Name == name);

    [Fact]
    public void Exposes_message_me_and_send_image()
    {
        var names = Make(new FakeSender()).AsTools().Select(t => ((AIFunction)t).Name).ToList();
        Assert.Contains("message_me", names);
        Assert.Contains("send_image", names);
    }

    [Fact]
    public async Task Send_image_sends_an_existing_file_to_the_owner()
    {
        var sender = new FakeSender();
        var file = Path.GetTempFileName();
        try
        {
            var result = ((JsonElement)(await Tool(Make(sender), "send_image")
                .InvokeAsync(new() { ["filePath"] = file, ["caption"] = "shot" }))!).GetString()!;

            Assert.Contains("delivered", result, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(sender.ImageCall);
            Assert.Equal(file, sender.ImageCall!.Value.Path);
            Assert.Equal("shot", sender.ImageCall.Value.Caption);
            Assert.Equal("4915123456789@s.whatsapp.net", sender.ImageCall.Value.Jid);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Send_image_refuses_a_missing_file()
    {
        var sender = new FakeSender();
        var result = ((JsonElement)(await Tool(Make(sender), "send_image")
            .InvokeAsync(new() { ["filePath"] = "/no/such/file.png" }))!).GetString()!;

        Assert.Contains("Cannot send", result);
        Assert.Null(sender.ImageCall);   // never reached the sender
    }

    [Fact]
    public async Task Send_image_refuses_when_owner_number_is_unconfigured()
    {
        var sender = new FakeSender();
        var tools = new NotifyTools(sender, Options.Create(new WhatsAppOptions { OwnerNumber = "" }));
        var result = ((JsonElement)(await Tool(tools, "send_image")
            .InvokeAsync(new() { ["filePath"] = "/any/path.png" }))!).GetString()!;

        Assert.Contains("not configured", result);
        Assert.Null(sender.ImageCall);   // guarded before touching the sender
    }
}
