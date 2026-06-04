using System.Net;
using System.Text.Json;
using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class WhatsAppSenderImageTests
{
    /// <summary>Captures the outbound request and returns a canned status.</summary>
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class FakeEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Erda.Tests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static WhatsAppSender Make(CapturingHandler handler, string env)
    {
        var opts = Options.Create(new WhatsAppOptions { BridgeUrl = "http://bridge:8088", SharedSecret = "s3cret" });
        return new WhatsAppSender(new HttpClient(handler), opts, new FakeEnv(env), NullLogger<WhatsAppSender>.Instance);
    }

    [Fact]
    public async Task Posts_to_send_media_with_secret_and_fields()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var ok = await Make(handler, Environments.Production)
            .SendImageAsync("4915123456789@s.whatsapp.net", "/media/shot.png", "the page");

        Assert.True(ok);
        Assert.EndsWith("/send-media", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("s3cret", handler.Request.Headers.GetValues("X-Bridge-Secret").Single());

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("4915123456789@s.whatsapp.net", doc.RootElement.GetProperty("to").GetString());
        Assert.Equal("/media/shot.png", doc.RootElement.GetProperty("mediaPath").GetString());
        Assert.Equal("the page", doc.RootElement.GetProperty("caption").GetString());
    }

    [Fact]
    public async Task Prefixes_the_caption_in_development()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        await Make(handler, Environments.Development)
            .SendImageAsync("1@s.whatsapp.net", "/media/shot.png", "hi");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.StartsWith("🧪", doc.RootElement.GetProperty("caption").GetString());
    }

    [Fact]
    public async Task Returns_false_on_a_non_success_status()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var ok = await Make(handler, Environments.Production)
            .SendImageAsync("1@s.whatsapp.net", "/media/shot.png", null);
        Assert.False(ok);
    }
}
