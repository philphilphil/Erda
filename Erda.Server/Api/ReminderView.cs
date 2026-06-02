using Erda.Scheduling;

namespace Erda.Api;

/// <summary>
/// Pure presentation helpers for reminders, lifted verbatim from the old <c>Reminders.razor</c> so
/// the next-fire formatting and the slug / unique-id generation are unit-testable without rendering.
/// The JSON API endpoints call these; behavior (including the UTC fallback for an unknown timezone)
/// must match what the Blazor panel produced.
/// </summary>
public static class ReminderView
{
    /// <summary>
    /// The human-readable next-fire string for a schedule, interpreted in <paramref name="zone"/>:
    /// for a cron schedule the next occurrence as <c>yyyy-MM-dd HH:mm</c> (or "never"); for a
    /// one-shot its wall-clock time (or "—" if somehow absent).
    /// </summary>
    public static string NextFire(WhenSpec spec, DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        if (spec.IsRecurring)
        {
            var occ = spec.Cron!.GetNextOccurrence(utcNow, zone);
            return occ is { } next ? next.ToString("yyyy-MM-dd HH:mm") : "never";
        }
        return spec.OneShotLocal is { } local ? local.ToString("yyyy-MM-dd HH:mm") : "—";
    }

    /// <summary>Resolve the configured IANA timezone, falling back to UTC if it is unknown.</summary>
    public static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Build a short, URL-safe slug from a reminder's text (first few words, transliterated umlauts,
    /// max 24 chars). Empty input yields "reminder".
    /// </summary>
    public static string Slugify(string text)
    {
        var n = text.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var words = n.Split([' ', '-', '_', ',', '.', '!', '?', ':', ';', '(', ')', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Take(5).Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0);
        var slug = string.Join("-", words);
        if (slug.Length > 24) slug = slug[..24].TrimEnd('-');
        return slug.Length > 0 ? slug : "reminder";
    }

    /// <summary>
    /// Return <paramref name="baseSlug"/> if free, else append <c>-2</c>, <c>-3</c>, … until unique
    /// against <paramref name="existing"/>.
    /// </summary>
    public static string UniqueId(string baseSlug, ISet<string> existing)
    {
        if (!existing.Contains(baseSlug)) return baseSlug;
        for (var n = 2; ; n++) { var c = $"{baseSlug}-{n}"; if (!existing.Contains(c)) return c; }
    }
}
