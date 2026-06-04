using Erda.Agents.Tools;
using Xunit;

namespace Erda.Tests;

public class RegistrableDomainTests
{
    [Theory]
    [InlineData("moxfield.com", "moxfield.com")]
    [InlineData("www.moxfield.com", "moxfield.com")]
    [InlineData("https://www.moxfield.com/decks/abc", "moxfield.com")]
    [InlineData("https://accounts.google.com/signin", "google.com")]
    [InlineData("HTTPS://WWW.Moxfield.COM", "moxfield.com")]
    [InlineData("foo.bar.co.uk", "bar.co.uk")]          // two-level public suffix
    [InlineData("shop.example.com.au", "example.com.au")]
    public void Of_returns_the_registrable_domain(string input, string expected)
        => Assert.Equal(expected, RegistrableDomain.Of(input));

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("not a url")]
    public void Of_returns_empty_for_unusable_input(string input)
        => Assert.Equal("", RegistrableDomain.Of(input));

    [Fact]
    public void Matches_is_case_insensitive_and_subdomain_tolerant()
    {
        Assert.True(RegistrableDomain.Matches("https://login.moxfield.com", "moxfield.com"));
        Assert.False(RegistrableDomain.Matches("https://example.com", "moxfield.com"));
    }
}
