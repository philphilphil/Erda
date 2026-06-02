using System.Net;
using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class WhatsAppSenderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status);
        }
    }

    private static WhatsAppSender Make(CapturingHandler handler, string secret = "s3cr3t") =>
        new(new HttpClient(handler),
            Options.Create(new WhatsAppOptions { BridgeUrl = "http://127.0.0.1:8088", SharedSecret = secret }),
            NullLogger<WhatsAppSender>.Instance);

    [Fact]
    public async Task Posts_to_send_with_secret_header_and_json_body()
    {
        var handler = new CapturingHandler();
        var sender = Make(handler);

        var ok = await sender.SendAsync("4915123456789@s.whatsapp.net", "hello");

        Assert.True(ok);
        Assert.Equal("http://127.0.0.1:8088/send", handler.Request!.RequestUri!.ToString());
        Assert.Equal("s3cr3t", handler.Request.Headers.GetValues("X-Bridge-Secret").Single());
        Assert.Contains("4915123456789@s.whatsapp.net", handler.Body);
        Assert.Contains("hello", handler.Body);
    }

    [Fact]
    public async Task Returns_false_on_non_success_status()
    {
        var handler = new CapturingHandler { Status = HttpStatusCode.Unauthorized };
        var sender = Make(handler);

        Assert.False(await sender.SendAsync("x@s.whatsapp.net", "hi"));
    }
}
