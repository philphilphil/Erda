using Erda.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Config is env-only and fail-fast: required values have no default and are validated at startup.
/// Credentials are validated by DataAnnotations; feature settings (WhatsApp, Browser) only when the
/// feature is enabled.
/// </summary>
public class ConfigValidationTests
{
    private static CredentialsOptions BindCredentials(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<CredentialsOptions>().Bind(config).ValidateDataAnnotations();
        return services.BuildServiceProvider().GetRequiredService<IOptions<CredentialsOptions>>().Value;
    }

    [Fact]
    public void Credentials_missing_keys_throw_on_access()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => BindCredentials(new()));
        Assert.Contains(nameof(CredentialsOptions.AzureOpenAIApiKey), string.Join(" ", ex.Failures));
    }

    [Fact]
    public void Credentials_all_present_bind_from_flat_env_keys()
    {
        var creds = BindCredentials(new()
        {
            ["AZURE_OPENAI_ENDPOINT"] = "https://x.services.ai.azure.com/",
            ["AZURE_OPENAI_API_KEY"] = "key1",
            ["OPENAI_API_KEY"] = "key2",
        });

        Assert.Equal("https://x.services.ai.azure.com/", creds.AzureOpenAIEndpoint);
        Assert.Equal("key1", creds.AzureOpenAIApiKey);
        Assert.Equal("key2", creds.OpenAIApiKey);
    }

    [Fact]
    public void WhatsApp_disabled_needs_nothing()
    {
        var result = new WhatsAppOptionsValidator().Validate(null, new WhatsAppOptions { Enabled = false });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void WhatsApp_enabled_lists_every_missing_key()
    {
        var result = new WhatsAppOptionsValidator().Validate(null, new WhatsAppOptions { Enabled = true });
        Assert.True(result.Failed);
        Assert.Contains("WhatsApp__OwnerNumber", result.FailureMessage);
        Assert.Contains("WhatsApp__SharedSecret", result.FailureMessage);
    }

    [Fact]
    public void WhatsApp_enabled_and_complete_passes()
    {
        var result = new WhatsAppOptionsValidator().Validate(null, new WhatsAppOptions
        {
            Enabled = true,
            OwnerNumber = "+490000000000",
            BridgeUrl = "http://bridge:8088",
            SharedSecret = "secret",
            MediaTempDir = "/media",
        });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Browser_enabled_requires_its_dirs()
    {
        var result = new BrowserOptionsValidator().Validate(null, new BrowserOptions { Enabled = true });
        Assert.True(result.Failed);
        Assert.Contains("UserDataDir", result.FailureMessage);
        Assert.Contains("OutputDir", result.FailureMessage);
    }

    [Fact]
    public void Browser_disabled_needs_nothing()
    {
        Assert.True(new BrowserOptionsValidator().Validate(null, new BrowserOptions { Enabled = false }).Succeeded);
    }
}
