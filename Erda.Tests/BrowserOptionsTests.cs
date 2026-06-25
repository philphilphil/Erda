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
                ["Erda:Browser:UserDataDir"] = "/data/browser",
                ["Erda:Browser:OutputDir"] = "/media",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<BrowserOptions>(config.GetSection("Erda:Browser"));
        var opts = services.BuildServiceProvider().GetRequiredService<IOptions<BrowserOptions>>().Value;

        Assert.True(opts.Enabled);
        Assert.Equal("gpt-5", opts.Deployment);
        Assert.Equal("/data/browser", opts.UserDataDir);
        Assert.Equal("/media", opts.OutputDir);
    }

    [Fact]
    public void Defaults_are_disabled_and_safe()
    {
        var opts = new BrowserOptions();
        Assert.False(opts.Enabled);
        Assert.Null(opts.Deployment);          // null => fall back to ChatModel
        Assert.Equal("", opts.UserDataDir);    // required when enabled; no default (validated at startup)
        Assert.Equal("", opts.OutputDir);      // required when enabled; no default
        Assert.True(opts.MaxSteps > 0);
        Assert.False(opts.ShowWindow);         // absent => headless (safe for the display-less Jetson)
    }

    [Fact]
    public void Default_McpArgs_select_bundled_chromium_no_sandbox()
    {
        // The MCP defaults to the `chrome` channel (branded Google Chrome), which the ARM64 runtime
        // image never installs — launching it fails with "Chromium distribution 'chrome' is not found".
        // The default args must pin the bundled Chromium and disable the sandbox so it launches in-container.
        var opts = new BrowserOptions();

        var browserIdx = Array.IndexOf(opts.McpArgs, "--browser");
        Assert.True(browserIdx >= 0 && browserIdx + 1 < opts.McpArgs.Length, "--browser flag missing");
        Assert.Equal("chromium", opts.McpArgs[browserIdx + 1]);
        Assert.Contains("--no-sandbox", opts.McpArgs);
    }
}
