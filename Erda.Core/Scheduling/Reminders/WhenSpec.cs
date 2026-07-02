using System.Globalization;
using Cronos;

namespace Erda.Core.Scheduling;

/// <summary>
/// A parsed reminder schedule from a reminder's <c>when</c> column. Either:
/// a one-shot wall-clock date-time (interpreted in the configured zone), or a recurring cron
/// expression. Parsing tries a date-time first (so plain timestamps never get read as cron),
/// then a cron expression (5-field standard or a macro like <c>@daily</c>).
/// </summary>
public sealed class WhenSpec
{
    // Minute-granularity one-shot formats; seconds tolerated but ignored by the scheduler.
    private static readonly string[] OneShotFormats =
    [
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
    ];

    private WhenSpec(CronExpression? cron, DateTime? oneShotLocal)
    {
        Cron = cron;
        OneShotLocal = oneShotLocal;
    }

    /// <summary>True when this is a recurring cron schedule.</summary>
    public bool IsRecurring => Cron is not null;

    /// <summary>The parsed cron expression, when <see cref="IsRecurring"/>; otherwise null.</summary>
    public CronExpression? Cron { get; }

    /// <summary>The one-shot wall-clock time (<see cref="DateTimeKind.Unspecified"/>), when not recurring.</summary>
    public DateTime? OneShotLocal { get; }

    public static bool TryParse(string? text, out WhenSpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();

        if (DateTime.TryParseExact(trimmed, OneShotFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            spec = new WhenSpec(null, DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
            return true;
        }

        try
        {
            spec = new WhenSpec(CronExpression.Parse(trimmed), null);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }

    /// <summary>Parse or throw; for callers that have already validated or want the exception.</summary>
    public static WhenSpec Parse(string text) =>
        TryParse(text, out var spec) ? spec! : throw new FormatException($"Unrecognized schedule: '{text}'.");

    /// <summary>The UTC instant a one-shot is due, interpreting its wall-clock time in <paramref name="zone"/>.</summary>
    public DateTimeOffset OneShotDueUtc(TimeZoneInfo zone)
    {
        if (OneShotLocal is null)
            throw new InvalidOperationException("OneShotDueUtc is only valid for a one-shot schedule.");
        var utc = TimeZoneInfo.ConvertTimeToUtc(OneShotLocal.Value, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
