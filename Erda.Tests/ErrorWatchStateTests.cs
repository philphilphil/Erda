using Erda.Scheduling;
using Xunit;

namespace Erda.Tests;

public class ErrorWatchStateTests
{
    [Fact]
    public void Trim_keeps_the_newest_within_bounds()
    {
        var s = new ErrorWatchState();
        for (var i = 0; i < 600; i++)
        {
            s.SeenSignatures.Add("s" + i);
            s.SeenEventIds.Add("e" + i);
        }

        s.Trim(maxSignatures: 100, maxEventIds: 50);

        Assert.Equal(100, s.SeenSignatures.Count);
        Assert.Equal(50, s.SeenEventIds.Count);
        Assert.Equal("s599", s.SeenSignatures[^1]); // newest retained
    }

    [Fact]
    public void Store_round_trips_state()
    {
        var store = new ErrorWatchStateStore(TestDb.NewFactory());
        store.Save(new ErrorWatchState
        {
            LastTimestampUtc = DateTimeOffset.UnixEpoch,
            SeenSignatures = { "a" },
            SeenEventIds = { "e1" },
        });

        var loaded = store.Load();

        Assert.Equal(DateTimeOffset.UnixEpoch, loaded.LastTimestampUtc);
        Assert.Contains("a", loaded.SeenSignatures);
        Assert.Contains("e1", loaded.SeenEventIds);
    }

    [Fact]
    public void Load_returns_fresh_state_when_missing()
    {
        var store = new ErrorWatchStateStore(TestDb.NewFactory());
        var s = store.Load();
        Assert.Null(s.LastTimestampUtc);
        Assert.Empty(s.SeenSignatures);
    }
}
