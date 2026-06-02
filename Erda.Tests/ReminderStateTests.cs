using Erda.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Erda.Tests;

public class ReminderStateTests
{
    [Fact]
    public void Round_trips_last_fired_and_fired_one_shots()
    {
        // Run-state now lives on the reminder rows, so the rows must exist first.
        var dbf = TestDb.NewFactory();
        var store = new ReminderStore(dbf, NullLogger<ReminderStore>.Instance);
        store.Append(ReminderKind.Prompt, "weather", "0 6 * * *", "Weather?");
        store.Append(ReminderKind.Reminder, "call-mom", "2026-06-15 09:00", "Call mom");

        var stateStore = new ReminderStateStore(dbf);
        var state = new ReminderState();
        state.LastFiredUtc["weather"] = new DateTimeOffset(2026, 6, 2, 4, 0, 0, TimeSpan.Zero);
        state.FiredOneShotIds.Add("call-mom");
        stateStore.Save(state);

        var loaded = new ReminderStateStore(dbf).Load();
        Assert.Equal(new DateTimeOffset(2026, 6, 2, 4, 0, 0, TimeSpan.Zero), loaded.LastFiredUtc["weather"]);
        Assert.Contains("call-mom", loaded.FiredOneShotIds);
    }

    [Fact]
    public void Load_returns_empty_state_when_no_rows()
    {
        var loaded = new ReminderStateStore(TestDb.NewFactory()).Load();
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
