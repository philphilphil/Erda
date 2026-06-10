using Microsoft.Extensions.Options;

namespace Erda.Core.Configuration;

/// <summary>
/// Startup validators for the feature options whose required settings only matter when the feature
/// is switched on. A disabled feature contributes nothing to validation, so <c>make dev</c> without
/// WhatsApp (or with the browser off) need not supply that feature's settings. When a feature IS
/// enabled, every listed value must be present and non-blank or the host refuses to start (these are
/// wired with <c>ValidateOnStart</c> in <see cref="ServiceCollectionExtensions"/>), and all missing
/// keys are reported together.
/// </summary>
internal static class RequiredWhenEnabled
{
    /// <summary>Builds a failure result listing every blank key, or success when none are missing.</summary>
    public static ValidateOptionsResult Check(string section, params (string Key, string? Value)[] required)
    {
        var missing = required
            .Where(r => string.IsNullOrWhiteSpace(r.Value))
            .Select(r => $"{section}__{r.Key}")
            .ToList();

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Missing required configuration (set in .env): {string.Join(", ", missing)}.");
    }
}

/// <summary>Requires the bridge/owner settings whenever the WhatsApp channel is enabled.</summary>
public sealed class WhatsAppOptionsValidator : IValidateOptions<WhatsAppOptions>
{
    public ValidateOptionsResult Validate(string? name, WhatsAppOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;
        return RequiredWhenEnabled.Check(WhatsAppOptions.SectionName,
            (nameof(o.OwnerNumber), o.OwnerNumber),
            (nameof(o.BridgeUrl), o.BridgeUrl),
            (nameof(o.SharedSecret), o.SharedSecret),
            (nameof(o.MediaTempDir), o.MediaTempDir));
    }
}

/// <summary>Requires the browser profile/output dirs whenever the agentic browser is enabled.</summary>
public sealed class BrowserOptionsValidator : IValidateOptions<BrowserOptions>
{
    public ValidateOptionsResult Validate(string? name, BrowserOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;
        // Env-var form of the nested section: Erda:Browser -> Erda__Browser__<Key>.
        return RequiredWhenEnabled.Check("Erda__Browser",
            (nameof(o.UserDataDir), o.UserDataDir),
            (nameof(o.OutputDir), o.OutputDir));
    }
}
