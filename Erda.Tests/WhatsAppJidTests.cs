using Erda.Channels;
using Xunit;

namespace Erda.Tests;

public class WhatsAppJidTests
{
    [Theory]
    [InlineData("+49 151 2345 6789", "4915123456789@s.whatsapp.net")]
    [InlineData("+491512345 6789", "4915123456789@s.whatsapp.net")]
    [InlineData("4915123456789@s.whatsapp.net", "4915123456789@s.whatsapp.net")]
    public void FromNumber_normalizes_to_jid(string input, string expected) =>
        Assert.Equal(expected, WhatsAppJid.FromNumber(input));

    [Fact]
    public void FromNumber_empty_for_blank() =>
        Assert.Equal("", WhatsAppJid.FromNumber("   "));

    [Theory]
    [InlineData("4915123456789@s.whatsapp.net", "4915123456789")]
    [InlineData("4915123456789:12@s.whatsapp.net", "4915123456789")] // device suffix stripped
    [InlineData("4915123456789", "4915123456789")]
    public void BareUser_strips_domain_and_device(string jid, string expected) =>
        Assert.Equal(expected, WhatsAppJid.BareUser(jid));

    [Fact]
    public void IsOwner_matches_across_formats()
    {
        Assert.True(WhatsAppJid.IsOwner("+49 151 2345 6789", "4915123456789@s.whatsapp.net"));
        Assert.True(WhatsAppJid.IsOwner("+49 151 2345 6789", "4915123456789:7@s.whatsapp.net"));
        Assert.False(WhatsAppJid.IsOwner("+49 151 2345 6789", "4900000000@s.whatsapp.net"));
        Assert.False(WhatsAppJid.IsOwner("", "4915123456789@s.whatsapp.net"));
    }

    [Theory]
    [InlineData("12345@g.us", true)]
    [InlineData("4915123456789@s.whatsapp.net", false)]
    public void IsGroup_detects_group_domain(string jid, bool expected) =>
        Assert.Equal(expected, WhatsAppJid.IsGroup(jid));
}
