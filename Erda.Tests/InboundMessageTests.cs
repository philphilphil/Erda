using System.Text.Json;
using Erda.WhatsApp;
using Xunit;

namespace Erda.Tests;

public class InboundMessageTests
{
    private static readonly JsonSerializerOptions Web = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("text", InboundKind.Text)]
    [InlineData("audio", InboundKind.Audio)]
    [InlineData("image", InboundKind.Image)]
    [InlineData("sticker", InboundKind.Unknown)]
    public void Kind_is_derived_from_type(string type, InboundKind expected) =>
        Assert.Equal(expected, new InboundMessage { Type = type }.Kind);

    [Fact]
    public void Deserializes_the_bridge_payload()
    {
        const string json = """
            {"from":"4915123456789@s.whatsapp.net","chat":"4915123456789@s.whatsapp.net",
             "type":"image","text":"a whiteboard","mediaPath":"/tmp/erda-bridge/x.jpg",
             "mimeType":"image/jpeg","messageId":"3EB0","timestamp":1748600000}
            """;

        var m = JsonSerializer.Deserialize<InboundMessage>(json, Web)!;

        Assert.Equal("4915123456789@s.whatsapp.net", m.From);
        Assert.Equal(InboundKind.Image, m.Kind);
        Assert.Equal("a whiteboard", m.Text);
        Assert.Equal("/tmp/erda-bridge/x.jpg", m.MediaPath);
        Assert.Equal("image/jpeg", m.MimeType);
        Assert.Equal(1748600000, m.Timestamp);
    }

    [Fact]
    public void Text_payload_omits_media_fields()
    {
        const string json = """{"from":"x@s.whatsapp.net","chat":"x@s.whatsapp.net","type":"text","text":"hi"}""";
        var m = JsonSerializer.Deserialize<InboundMessage>(json, Web)!;
        Assert.Equal(InboundKind.Text, m.Kind);
        Assert.Null(m.MediaPath);
        Assert.Null(m.MimeType);
    }
}
