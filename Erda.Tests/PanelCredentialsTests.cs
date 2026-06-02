using Erda.Server.Api;
using Erda.Core.Configuration;
using Xunit;

namespace Erda.Tests;

/// <summary>Covers the pure login credential check and the "auth off by default" switch.</summary>
public class PanelCredentialsTests
{
    [Fact]
    public void AuthRequired_is_false_when_no_password()
    {
        Assert.False(new PanelOptions { Password = null }.AuthRequired);
        Assert.False(new PanelOptions { Password = "" }.AuthRequired);
        Assert.False(new PanelOptions { Password = "   " }.AuthRequired);
    }

    [Fact]
    public void AuthRequired_is_true_when_password_set()
    {
        Assert.True(new PanelOptions { Password = "secret" }.AuthRequired);
    }

    [Fact]
    public void Valid_when_username_and_password_match()
    {
        var panel = new PanelOptions { Username = "admin", Password = "secret" };
        Assert.True(PanelCredentials.IsValid(panel, "admin", "secret"));
    }

    [Fact]
    public void Invalid_on_wrong_password_or_username()
    {
        var panel = new PanelOptions { Username = "admin", Password = "secret" };
        Assert.False(PanelCredentials.IsValid(panel, "admin", "wrong"));
        Assert.False(PanelCredentials.IsValid(panel, "intruder", "secret"));
    }

    [Fact]
    public void Username_is_ignored_when_not_configured()
    {
        var panel = new PanelOptions { Username = "", Password = "secret" };
        Assert.True(PanelCredentials.IsValid(panel, username: null, password: "secret"));
        Assert.True(PanelCredentials.IsValid(panel, username: "anything", password: "secret"));
        Assert.False(PanelCredentials.IsValid(panel, username: "anything", password: "nope"));
    }
}
