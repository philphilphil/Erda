using Erda.Server.Api;
using Erda.Core.Scheduling;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Covers the presentation helpers ported out of the old <c>Reminders.razor</c>: next-fire
/// formatting, the timezone fallback, slug generation, and unique-id collision handling. These
/// must match the Blazor behavior so the JSON API produces the same strings/ids.
/// </summary>
public class ReminderViewTests
{
    [Fact]
    public void NextFire_one_shot_returns_its_wall_clock_time()
    {
        var spec = WhenSpec.Parse("2026-06-15 09:00");
        var result = ReminderView.NextFire(spec, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal("2026-06-15 09:00", result);
    }

    [Fact]
    public void NextFire_cron_returns_next_occurrence_in_zone()
    {
        var spec = WhenSpec.Parse("0 6 * * *"); // daily at 06:00
        var now = new DateTimeOffset(2026, 6, 15, 3, 0, 0, TimeSpan.Zero); // 03:00 UTC, before 06:00
        var result = ReminderView.NextFire(spec, now, TimeZoneInfo.Utc);
        Assert.Equal("2026-06-15 06:00", result);
    }

    [Fact]
    public void NextFire_cron_rolls_to_tomorrow_when_today_is_past()
    {
        var spec = WhenSpec.Parse("0 6 * * *");
        var now = new DateTimeOffset(2026, 6, 15, 7, 0, 0, TimeSpan.Zero); // 07:00 UTC, after 06:00
        var result = ReminderView.NextFire(spec, now, TimeZoneInfo.Utc);
        Assert.Equal("2026-06-16 06:00", result);
    }

    [Fact]
    public void ResolveZone_returns_the_zone_when_known()
    {
        var zone = ReminderView.ResolveZone("Europe/Berlin");
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"), zone);
    }

    [Fact]
    public void ResolveZone_falls_back_to_utc_when_unknown()
    {
        Assert.Equal(TimeZoneInfo.Utc, ReminderView.ResolveZone("Not/AZone"));
    }

    [Theory]
    [InlineData("Call mom", "call-mom")]
    [InlineData("Take out the trash", "take-out-the-trash")]
    [InlineData("Müll rausbringen", "muell-rausbringen")]
    [InlineData("   ", "reminder")]
    public void Slugify_produces_expected_slug(string text, string expected)
    {
        Assert.Equal(expected, ReminderView.Slugify(text));
    }

    [Fact]
    public void Slugify_caps_length_at_24_chars()
    {
        var slug = ReminderView.Slugify("supercalifragilistic expialidocious extravaganza");
        Assert.True(slug.Length <= 24, $"slug was '{slug}' ({slug.Length} chars)");
    }

    [Fact]
    public void UniqueId_returns_base_when_free()
    {
        Assert.Equal("fresh", ReminderView.UniqueId("fresh", new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void UniqueId_appends_suffix_on_collision()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "call-mom", "call-mom-2" };
        Assert.Equal("call-mom-3", ReminderView.UniqueId("call-mom", existing));
    }
}
