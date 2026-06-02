using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Server.Api;

/// <summary>
/// Backs the Config screen: owns the allowlist of editable runtime keys and the read/write rules for
/// the <c>ConfigOverrides</c> table. Lifted out of the old <c>ConfigEditor.razor</c> so the
/// effective-vs-override resolution and the save/clear logic are unit-testable. Overrides are applied
/// on restart (the SQLite configuration provider reads them once at startup), matching v1 behavior.
/// </summary>
public sealed class ConfigPanelService(IConfiguration config, IDbContextFactory<ErdaDbContext> dbFactory)
{
    /// <summary>One editable knob: a config <see cref="Key"/> in <c>Section:Key</c> form, plus UI labels.</summary>
    public sealed record AllowlistItem(string Label, string Key, string Hint);

    /// <summary>
    /// The keys the panel may edit. Anything not here is ignored by <see cref="Apply"/> and never
    /// returned by <see cref="GetItems"/> — a deliberate guard so the UI can't write arbitrary config.
    /// </summary>
    public static readonly IReadOnlyList<AllowlistItem> Allowlist =
    [
        new("Codex reasoning effort", "Erda:CodexReasoningEffort", "low / medium / high"),
        new("Chat model deployment", "Erda:ChatDeployment", ""),
        new("Error-watch enabled", "ErrorWatch:Enabled", "true / false"),
        new("Error-watch min level", "ErrorWatch:MinLevel", "Error / Warning / …"),
        new("Error-watch max alerts/poll", "ErrorWatch:MaxAlertsPerPoll", "numeric"),
        new("Error-watch poll interval", "ErrorWatch:PollInterval", "00:05:00"),
        new("Reminders enabled", "Reminders:Enabled", "true / false"),
        new("Reminders poll interval", "Reminders:PollInterval", "00:01:00"),
        new("Reminders timezone", "Reminders:TimeZone", "Europe/Berlin"),
    ];

    /// <summary>
    /// The allowlisted knobs with their running (<c>effective</c>) value, the prefill <c>value</c>
    /// (pending DB override if present, else effective), and whether an override row exists.
    /// </summary>
    public IReadOnlyList<ConfigItemDto> GetItems()
    {
        using var db = dbFactory.CreateDbContext();
        var overrides = db.ConfigOverrides.ToDictionary(c => c.Key, c => c.Value);

        return Allowlist.Select(item =>
        {
            var effective = config[item.Key];
            var hasOverride = overrides.TryGetValue(item.Key, out var ov);
            var value = hasOverride ? ov : effective;
            return new ConfigItemDto(item.Key, item.Label, item.Hint, value, effective, hasOverride);
        }).ToList();
    }

    /// <summary>
    /// Set or clear overrides for the supplied keys (only allowlisted keys present in
    /// <paramref name="values"/> are touched). An override row is written only when the value differs
    /// from the effective config and is non-blank; otherwise any existing override row is removed.
    /// Mirrors the old Blazor save/clear behavior exactly. Returns the number of rows touched.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string?> values)
    {
        using var db = dbFactory.CreateDbContext();

        foreach (var item in Allowlist)
        {
            if (!values.TryGetValue(item.Key, out var value))
                continue; // only act on keys the caller actually sent

            var effective = config[item.Key];
            var existing = db.ConfigOverrides.FirstOrDefault(c => c.Key == item.Key);

            var isDifferent = !string.Equals(value, effective, StringComparison.Ordinal);
            var isBlank = string.IsNullOrWhiteSpace(value);

            if (isDifferent && !isBlank)
            {
                if (existing is null)
                    db.ConfigOverrides.Add(new ConfigOverride { Key = item.Key, Value = value });
                else
                    existing.Value = value;
            }
            else if (existing is not null)
            {
                db.ConfigOverrides.Remove(existing);
            }
        }

        db.SaveChanges();
    }
}
