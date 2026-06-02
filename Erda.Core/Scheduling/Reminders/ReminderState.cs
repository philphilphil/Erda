using Erda.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Scheduling;

/// <summary>
/// Machine-only run state for the reminder scheduler. Drives recurring cadence and guarantees a
/// one-shot is sent at most once even if the status write fails. Held in memory during a poll and
/// persisted by <see cref="ReminderStateStore"/> onto the reminder rows themselves.
/// </summary>
public sealed class ReminderState
{
    /// <summary>Per recurring reminder id, the last UTC instant it was fired.</summary>
    public Dictionary<string, DateTimeOffset> LastFiredUtc { get; set; } = new();

    /// <summary>One-shot ids already fired (the send-once authority); bounded by <see cref="Trim"/>.</summary>
    public List<string> FiredOneShotIds { get; set; } = [];

    /// <summary>Keep the fired-one-shot list from growing without bound (newest kept).</summary>
    public void Trim(int maxOneShots = 1000)
    {
        if (FiredOneShotIds.Count > maxOneShots)
            FiredOneShotIds.RemoveRange(0, FiredOneShotIds.Count - maxOneShots);
    }
}

/// <summary>
/// Loads/saves <see cref="ReminderState"/> against the reminder rows' run-state columns
/// (<c>LastFiredUtc</c>, <c>Fired</c>) in SQLite. Replaces the old JSON sidecar — so run-state now
/// survives container redeploys. Best-effort: a failure logs and leaves state unchanged rather than
/// breaking a poll.
/// </summary>
public sealed class ReminderStateStore(IDbContextFactory<ErdaDbContext> dbFactory, ILogger? logger = null)
{
    public ReminderState Load()
    {
        try
        {
            using var db = dbFactory.CreateDbContext();
            var state = new ReminderState();
            foreach (var r in db.Reminders.AsNoTracking())
            {
                if (r.LastFiredUtc is { } lastFired)
                    state.LastFiredUtc[r.Id] = lastFired;
                if (r.Fired)
                    state.FiredOneShotIds.Add(r.Id);
            }
            return state;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load reminder state; starting fresh.");
            return new ReminderState();
        }
    }

    public void Save(ReminderState state)
    {
        try
        {
            using var db = dbFactory.CreateDbContext();
            var fired = new HashSet<string>(state.FiredOneShotIds);
            foreach (var row in db.Reminders)
            {
                if (state.LastFiredUtc.TryGetValue(row.Id, out var lastFired))
                    row.LastFiredUtc = lastFired;
                if (fired.Contains(row.Id))
                    row.Fired = true;
            }
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not save reminder state.");
        }
    }
}
