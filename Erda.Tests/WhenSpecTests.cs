using Erda.Scheduling;
using Xunit;

namespace Erda.Tests;

public class WhenSpecTests
{
    [Fact]
    public void Parses_one_shot_date_time()
    {
        Assert.True(WhenSpec.TryParse("2026-06-15 09:00", out var spec));
        Assert.False(spec!.IsRecurring);
        Assert.Equal(new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Unspecified), spec.OneShotLocal);
    }

    [Fact]
    public void Parses_iso_t_separator()
    {
        Assert.True(WhenSpec.TryParse("2026-06-15T09:00", out var spec));
        Assert.False(spec!.IsRecurring);
    }

    [Fact]
    public void Parses_cron_expression()
    {
        Assert.True(WhenSpec.TryParse("0 6 * * *", out var spec));
        Assert.True(spec!.IsRecurring);
        Assert.NotNull(spec.Cron);
    }

    [Fact]
    public void Parses_cron_macro()
    {
        Assert.True(WhenSpec.TryParse("@daily", out var spec));
        Assert.True(spec!.IsRecurring);
    }

    [Fact]
    public void Parses_named_day_cron()
    {
        Assert.True(WhenSpec.TryParse("0 9 * * MON-FRI", out var spec));
        Assert.True(spec!.IsRecurring);
    }

    [Fact]
    public void Rejects_garbage()
    {
        Assert.False(WhenSpec.TryParse("not a schedule", out var spec));
        Assert.Null(spec);
    }

    [Fact]
    public void Rejects_empty()
    {
        Assert.False(WhenSpec.TryParse("   ", out _));
    }

    [Fact]
    public void One_shot_due_utc_respects_summer_time()
    {
        // Europe/Berlin in June is UTC+2, so 09:00 local == 07:00 UTC.
        Assert.True(WhenSpec.TryParse("2026-06-15 09:00", out var spec));
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.Equal(
            new DateTimeOffset(2026, 6, 15, 7, 0, 0, TimeSpan.Zero),
            spec!.OneShotDueUtc(berlin));
    }

    [Fact]
    public void One_shot_due_utc_respects_winter_time()
    {
        // Europe/Berlin in January is UTC+1, so 09:00 local == 08:00 UTC.
        Assert.True(WhenSpec.TryParse("2026-01-15 09:00", out var spec));
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
            spec!.OneShotDueUtc(berlin));
    }
}
