using Microsoft.Extensions.Options;

namespace Erda.Core.Configuration;

/// <summary>
/// Startup validators for feature options whose required settings only matter when the feature is
/// switched on. A disabled feature contributes nothing to validation, so <c>make dev</c> without a
/// feature need not supply its settings. When a feature IS enabled, every listed value must be
/// present (non-blank string, positive interval/count) or the host refuses to start (wired with
/// <c>ValidateOnStart</c> in <see cref="ServiceCollectionExtensions"/>), reporting all at once.
/// </summary>
internal static class RequiredWhenEnabled
{
    /// <summary>Fails listing every key whose <c>Ok</c> is false (env-var form), else succeeds.</summary>
    public static ValidateOptionsResult Check(string section, params (string Key, bool Ok)[] checks)
    {
        var missing = checks.Where(c => !c.Ok).Select(c => $"{section}__{c.Key}").ToList();
        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Missing/invalid required configuration (set in .env): {string.Join(", ", missing)}.");
    }

    public static bool Str(string? v) => !string.IsNullOrWhiteSpace(v);
    public static bool Pos(TimeSpan v) => v > TimeSpan.Zero;
    public static bool Pos(int v) => v > 0;
}

/// <summary>Requires the bridge/owner settings whenever the WhatsApp channel is enabled.</summary>
public sealed class WhatsAppOptionsValidator : IValidateOptions<WhatsAppOptions>
{
    public ValidateOptionsResult Validate(string? name, WhatsAppOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;
        return RequiredWhenEnabled.Check(WhatsAppOptions.SectionName,
            (nameof(o.OwnerNumber), RequiredWhenEnabled.Str(o.OwnerNumber)),
            (nameof(o.BridgeUrl), RequiredWhenEnabled.Str(o.BridgeUrl)),
            (nameof(o.SharedSecret), RequiredWhenEnabled.Str(o.SharedSecret)),
            (nameof(o.MediaTempDir), RequiredWhenEnabled.Str(o.MediaTempDir)));
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
            (nameof(o.UserDataDir), RequiredWhenEnabled.Str(o.UserDataDir)),
            (nameof(o.OutputDir), RequiredWhenEnabled.Str(o.OutputDir)));
    }
}

/// <summary>Requires the poll interval / level / cap whenever the error-watch scheduler is enabled.</summary>
public sealed class ErrorWatchOptionsValidator : IValidateOptions<ErrorWatchOptions>
{
    public ValidateOptionsResult Validate(string? name, ErrorWatchOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;
        return RequiredWhenEnabled.Check(ErrorWatchOptions.SectionName,
            (nameof(o.PollInterval), RequiredWhenEnabled.Pos(o.PollInterval)),
            (nameof(o.MinLevel), RequiredWhenEnabled.Str(o.MinLevel)),
            (nameof(o.MaxAlertsPerPoll), RequiredWhenEnabled.Pos(o.MaxAlertsPerPoll)));
    }
}

/// <summary>Requires the note path / timezone / intervals when reminders are enabled (and the
/// pre-script limits when pre-run scripts are enabled).</summary>
public sealed class ReminderOptionsValidator : IValidateOptions<ReminderOptions>
{
    public ValidateOptionsResult Validate(string? name, ReminderOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;

        var checks = new List<(string, bool)>
        {
            (nameof(o.NotePath), RequiredWhenEnabled.Str(o.NotePath)),
            (nameof(o.TimeZone), RequiredWhenEnabled.Str(o.TimeZone)),
            (nameof(o.PollInterval), RequiredWhenEnabled.Pos(o.PollInterval)),
            (nameof(o.OverdueGrace), RequiredWhenEnabled.Pos(o.OverdueGrace)),
        };
        if (o.PreScriptEnabled)
        {
            checks.Add((nameof(o.PreScriptTimeout), RequiredWhenEnabled.Pos(o.PreScriptTimeout)));
            checks.Add((nameof(o.PreScriptMaxOutputChars), RequiredWhenEnabled.Pos(o.PreScriptMaxOutputChars)));
        }
        return RequiredWhenEnabled.Check(ReminderOptions.SectionName, checks.ToArray());
    }
}
