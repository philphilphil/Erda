using Erda.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class BrowserOptionsTests
{
    [Fact]
    public void Binds_from_Erda_Browser_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Erda:Browser:Enabled"] = "true",
                ["Erda:Browser:Deployment"] = "gpt-5",
                ["Erda:Browser:McpCommand"] = "npx",
                ["Erda:Browser:UserDataDir"] = "/data/browser",
                ["Erda:Browser:MaxSteps"] = "25",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<BrowserOptions>(config.GetSection("Erda:Browser"));
        var opts = services.BuildServiceProvider().GetRequiredService<IOptions<BrowserOptions>>().Value;

        Assert.True(opts.Enabled);
        Assert.Equal("gpt-5", opts.Deployment);
        Assert.Equal("/data/browser", opts.UserDataDir);
        Assert.Equal(25, opts.MaxSteps);
    }

    [Fact]
    public void Defaults_are_disabled_and_safe()
    {
        var opts = new BrowserOptions();
        Assert.False(opts.Enabled);
        Assert.Null(opts.Deployment);          // null => fall back to ChatDeployment
        Assert.Equal("/data/browser", opts.UserDataDir);
        Assert.True(opts.MaxSteps > 0);
        Assert.True(opts.Headless);            // headless by default; flip to false on dev to watch
    }
}
