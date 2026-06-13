using Erda.Core.Data;
using Erda.Core.Scheduling;
using Xunit;

namespace Erda.Tests;

public class ErrorWatchStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Trim_keeps_the_most_recently_alerted_signatures()
    {
        var s = new ErrorWatchState();
        for (var i = 0; i < 600; i++)
        {
            s.SignatureLastAlerted["s" + i] = T0.AddMinutes(i); // higher i = more recent
            s.SeenEventIds.Add("e" + i);
        }

        s.Trim(maxSignatures: 100, maxEventIds: 50);

        Assert.Equal(100, s.SignatureLastAlerted.Count);
        Assert.Equal(50, s.SeenEventIds.Count);
        Assert.True(s.SignatureLastAlerted.ContainsKey("s599"));  // newest retained
        Assert.False(s.SignatureLastAlerted.ContainsKey("s0"));   // oldest dropped
    }

    [Fact]
    public void Store_round_trips_state()
    {
        var store = new ErrorWatchStateStore(TestDb.NewFactory());
        store.Save(new ErrorWatchState
        {
            LastTimestampUtc = DateTimeOffset.UnixEpoch,
            SignatureLastAlerted = { ["a"] = T0 },
            SeenEventIds = { "e1" },
        });

        var loaded = store.Load();

        Assert.Equal(DateTimeOffset.UnixEpoch, loaded.LastTimestampUtc);
        Assert.Equal(T0, loaded.SignatureLastAlerted["a"]);
        Assert.Contains("e1", loaded.SeenEventIds);
    }

    [Fact]
    public void Load_returns_fresh_state_when_missing()
    {
        var store = new ErrorWatchStateStore(TestDb.NewFactory());
        var s = store.Load();
        Assert.Null(s.LastTimestampUtc);
        Assert.Empty(s.SignatureLastAlerted);
    }

    [Theory]
    [InlineData("")]   // the value EF's AddColumn migration backfills into existing prod rows
    [InlineData("{}")] // an explicitly-empty map
    public void Load_migrates_a_legacy_seen_signatures_list_stamped_at_the_watermark(string newColumnValue)
    {
        // Pre-cooldown rows stored signatures as a bare list with no alert times. On load they should
        // migrate to last-alerted = the watermark, so the cooldown starts fresh (no replay burst) —
        // and a blank new column (the EF backfill) must not throw away the legacy memory.
        var factory = TestDb.NewFactory();
        using (var db = factory.CreateDbContext())
        {
            db.ErrorWatchState.Add(new ErrorWatchRow
            {
                Id = 1,
                LastTimestampUtc = T0,
                SeenSignaturesJson = "[\"legacy-sig\"]",
                SignatureLastAlertedJson = newColumnValue,
            });
            db.SaveChanges();
        }

        var loaded = new ErrorWatchStateStore(factory).Load();

        Assert.True(loaded.SignatureLastAlerted.ContainsKey("legacy-sig"));
        Assert.Equal(T0, loaded.SignatureLastAlerted["legacy-sig"]);
    }
}
