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
        Assert.Contains(nameof(CredentialsOptions.OpenAIApiKey), string.Join(" ", ex.Failures));
    }

    [Fact]
    public void Credentials_all_present_bind_from_flat_env_keys()
    {
        var creds = BindCredentials(new()
        {
            ["OPENAI_API_KEY"] = "key2",
        });

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

    [Fact]
    public void ErrorWatch_enabled_requires_interval_level_cap()
    {
        var result = new ErrorWatchOptionsValidator().Validate(null, new ErrorWatchOptions { Enabled = true });
        Assert.True(result.Failed);
        Assert.Contains("ErrorWatch__PollInterval", result.FailureMessage);   // unset TimeSpan = 00:00:00
        Assert.Contains("ErrorWatch__MinLevel", result.FailureMessage);
        Assert.Contains("ErrorWatch__MaxAlertsPerPoll", result.FailureMessage); // unset int = 0
    }

    [Fact]
    public void Reminders_enabled_requires_core_settings_and_prescript_limits_only_when_prescript_on()
    {
        var noPreScript = new ReminderOptionsValidator().Validate(null, new ReminderOptions { Enabled = true });
        Assert.True(noPreScript.Failed);
        Assert.Contains("Reminders__TimeZone", noPreScript.FailureMessage);
        Assert.Contains("Reminders__PollInterval", noPreScript.FailureMessage);
        Assert.DoesNotContain("PreScriptTimeout", noPreScript.FailureMessage); // not required while PreScript off

        var withPreScript = new ReminderOptionsValidator().Validate(null,
            new ReminderOptions { Enabled = true, PreScriptEnabled = true });
        Assert.Contains("Reminders__PreScriptTimeout", withPreScript.FailureMessage);
        Assert.Contains("Reminders__PreScriptMaxOutputChars", withPreScript.FailureMessage);
    }

    [Fact]
    public void Reminders_disabled_needs_nothing()
    {
        Assert.True(new ReminderOptionsValidator().Validate(null, new ReminderOptions { Enabled = false }).Succeeded);
    }

    [Fact]
    public void AppleBridge_disabled_needs_nothing()
    {
        var result = new AppleBridgeOptionsValidator().Validate(null, new AppleBridgeOptions { Enabled = false });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AppleBridge_enabled_lists_every_missing_key()
    {
        var result = new AppleBridgeOptionsValidator().Validate(null, new AppleBridgeOptions { Enabled = true });
        Assert.True(result.Failed);
        Assert.Contains("AppleBridge__BaseUrl", result.FailureMessage);
        Assert.Contains("AppleBridge__ApiKey", result.FailureMessage);
        Assert.Contains("AppleBridge__TimeoutSeconds", result.FailureMessage); // unset int = 0
    }

    [Fact]
    public void AppleBridge_enabled_and_complete_passes()
    {
        var result = new AppleBridgeOptionsValidator().Validate(null, new AppleBridgeOptions
        {
            Enabled = true,
            BaseUrl = "http://192.168.178.106:17832",
            ApiKey = "secret",
            TimeoutSeconds = 5,
        });
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Erda_options_require_models_and_chat_settings()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Erda:VaultPath"] = "/vault",
            ["Erda:DbPath"] = "/db",
            // models + chat settings deliberately omitted
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<ErdaOptions>().Bind(config.GetSection(ErdaOptions.SectionName)).ValidateDataAnnotations();

        var ex = Assert.Throws<OptionsValidationException>(
            () => services.BuildServiceProvider().GetRequiredService<IOptions<ErdaOptions>>().Value);
        var failures = string.Join(" ", ex.Failures);
        Assert.Contains(nameof(ErdaOptions.ChatBaseUrl), failures);
        Assert.Contains(nameof(ErdaOptions.ChatModel), failures);
        Assert.Contains(nameof(ErdaOptions.ChatReasoningEffort), failures);
    }
}
