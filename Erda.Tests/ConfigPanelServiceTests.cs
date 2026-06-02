using Erda.Server.Api;
using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Covers the effective-vs-override resolution and the save/clear rules ported out of the old
/// <c>ConfigEditor.razor</c>. The DB-backed override store is a throwaway SQLite file per test.
/// </summary>
public class ConfigPanelServiceTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ErrorWatch:MinLevel"] = "Error",
            ["Reminders:TimeZone"] = "Europe/Berlin",
        }).Build();

    private static (ConfigPanelService svc, IDbContextFactory<ErdaDbContext> db) New()
    {
        var factory = TestDb.NewFactory();
        return (new ConfigPanelService(Config(), factory), factory);
    }

    [Fact]
    public void GetItems_returns_the_full_allowlist_with_effective_values()
    {
        var (svc, _) = New();
        var items = svc.GetItems();

        Assert.Equal(ConfigPanelService.Allowlist.Count, items.Count);
        var minLevel = items.Single(i => i.Key == "ErrorWatch:MinLevel");
        Assert.Equal("Error", minLevel.Effective);
        Assert.Equal("Error", minLevel.Value);
        Assert.False(minLevel.Overridden);
    }

    [Fact]
    public void Apply_writes_an_override_when_value_differs_and_is_non_blank()
    {
        var (svc, _) = New();
        svc.Apply(new Dictionary<string, string?> { ["ErrorWatch:MinLevel"] = "Warning" });

        var item = svc.GetItems().Single(i => i.Key == "ErrorWatch:MinLevel");
        Assert.True(item.Overridden);
        Assert.Equal("Warning", item.Value);   // pending override shown in the input
        Assert.Equal("Error", item.Effective);  // running value unchanged until restart
    }

    [Fact]
    public void Apply_clears_an_override_when_value_equals_effective()
    {
        var (svc, _) = New();
        svc.Apply(new Dictionary<string, string?> { ["ErrorWatch:MinLevel"] = "Warning" });
        svc.Apply(new Dictionary<string, string?> { ["ErrorWatch:MinLevel"] = "Error" }); // back to effective

        Assert.False(svc.GetItems().Single(i => i.Key == "ErrorWatch:MinLevel").Overridden);
    }

    [Fact]
    public void Apply_clears_an_override_when_value_is_blank()
    {
        var (svc, _) = New();
        svc.Apply(new Dictionary<string, string?> { ["ErrorWatch:MinLevel"] = "Warning" });
        svc.Apply(new Dictionary<string, string?> { ["ErrorWatch:MinLevel"] = "" });

        Assert.False(svc.GetItems().Single(i => i.Key == "ErrorWatch:MinLevel").Overridden);
    }

    [Fact]
    public void Apply_ignores_keys_outside_the_allowlist()
    {
        var (svc, factory) = New();
        svc.Apply(new Dictionary<string, string?> { ["Totally:Unknown"] = "value" });

        using var db = factory.CreateDbContext();
        Assert.Empty(db.ConfigOverrides.ToList());
    }

    [Fact]
    public void Apply_only_touches_keys_present_in_the_request()
    {
        var (svc, _) = New();
        svc.Apply(new Dictionary<string, string?> { ["ErrorWatch:MinLevel"] = "Warning" });
        // A second apply that doesn't mention MinLevel must leave its override intact.
        svc.Apply(new Dictionary<string, string?> { ["Reminders:TimeZone"] = "UTC" });

        Assert.True(svc.GetItems().Single(i => i.Key == "ErrorWatch:MinLevel").Overridden);
    }
}
