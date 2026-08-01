using Erda.Core.Configuration;
using Erda.Server.Api;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// The Config screen is read-only now (env-only config). These cover that <see cref="ConfigPanelService"/>
/// projects the effective option values into grouped rows and never echoes a secret value.
/// </summary>
public class ConfigPanelServiceTests
{
    private static ConfigPanelService New(
        ErdaOptions? erda = null,
        WhatsAppOptions? whatsApp = null,
        SeqOptions? seq = null) =>
        new(
            Options.Create(erda ?? new ErdaOptions { VaultPath = "/vault", DbPath = "/data/erda.db" }),
            Options.Create(whatsApp ?? new WhatsAppOptions()),
            Options.Create(seq ?? new SeqOptions()),
            Options.Create(new ErrorWatchOptions()),
            Options.Create(new ReminderOptions()),
            Options.Create(new ObservabilityOptions()),
            Options.Create(new BrowserOptions()),
            Options.Create(new UploadOptions()),
            Options.Create(new AppleBridgeOptions()));

    [Fact]
    public void GetItems_projects_effective_option_values()
    {
        var items = New(erda: new ErdaOptions { VaultPath = "/my/vault", DbPath = "/db", ChatModel = "gpt-5-mini" }).GetItems();

        Assert.Equal("/my/vault", items.Single(i => i.Label == "Vault path").Value);
        Assert.Equal("gpt-5-mini", items.Single(i => i.Label == "Chat model").Value);
    }

    [Fact]
    public void GetItems_masks_secrets_and_never_echoes_them()
    {
        var secret = "super-secret-value";
        var items = New(whatsApp: new WhatsAppOptions { SharedSecret = secret }, seq: new SeqOptions { ApiKey = secret }).GetItems();

        Assert.Equal("(set)", items.Single(i => i.Label == "Shared secret").Value);
        Assert.Equal("(set)", items.Single(i => i.Label == "API key").Value);
        Assert.DoesNotContain(items, i => i.Value.Contains(secret));
    }

    [Fact]
    public void GetItems_shows_not_set_for_blank_values()
    {
        var items = New(seq: new SeqOptions { ServerUrl = null, ApiKey = null }).GetItems();

        Assert.Equal("(not set)", items.Single(i => i.Label == "Server URL").Value);
        Assert.Equal("(not set)", items.Single(i => i.Label == "API key").Value);
    }

    [Fact]
    public void GetItems_assigns_every_row_a_nonempty_group()
    {
        Assert.All(New().GetItems(), i => Assert.False(string.IsNullOrWhiteSpace(i.Group)));
    }
}
