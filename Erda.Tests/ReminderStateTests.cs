using Erda.Scheduling;
using Xunit;

namespace Erda.Tests;

public class ReminderStateTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Round_trips_last_fired_and_fired_one_shots()
    {
        var path = TempPath();
        var store = new ReminderStateStore(path);
        var state = new ReminderState();
        state.LastFiredUtc["weather"] = new DateTimeOffset(2026, 6, 2, 4, 0, 0, TimeSpan.Zero);
        state.FiredOneShotIds.Add("call-mom");
        store.Save(state);

        var loaded = new ReminderStateStore(path).Load();
        Assert.Equal(new DateTimeOffset(2026, 6, 2, 4, 0, 0, TimeSpan.Zero), loaded.LastFiredUtc["weather"]);
        Assert.Contains("call-mom", loaded.FiredOneShotIds);
    }

    [Fact]
    public void Load_returns_empty_state_when_file_missing()
    {
        var loaded = new ReminderStateStore(TempPath()).Load();
        Assert.Empty(loaded.LastFiredUtc);
        Assert.Empty(loaded.FiredOneShotIds);
    }

    [Fact]
    public void Trim_bounds_fired_one_shots_keeping_newest()
    {
        var state = new ReminderState();
        for (var i = 0; i < 10; i++)
            state.FiredOneShotIds.Add($"id-{i}");

        state.Trim(maxOneShots: 4);

        Assert.Equal(4, state.FiredOneShotIds.Count);
        Assert.Contains("id-9", state.FiredOneShotIds);
        Assert.DoesNotContain("id-0", state.FiredOneShotIds);
    }
}
