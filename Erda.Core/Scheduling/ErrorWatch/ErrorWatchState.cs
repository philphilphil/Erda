using System.Text.Json;
using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Core.Scheduling;

/// <summary>Persisted watermark + dedup memory for the error-watch scheduler.</summary>
public sealed class ErrorWatchState
{
    /// <summary>Newest event timestamp processed so far; the next poll queries from here.</summary>
    public DateTimeOffset? LastTimestampUtc { get; set; }

    /// <summary>Signatures already alerted on (bounded), so recurrences don't re-alert.</summary>
    public List<string> SeenSignatures { get; set; } = [];

    /// <summary>Recently processed event ids (bounded), to skip boundary duplicates across polls.</summary>
    public List<string> SeenEventIds { get; set; } = [];

    /// <summary>Keep the seen lists from growing without bound.</summary>
    public void Trim(int maxSignatures = 500, int maxEventIds = 500)
    {
        if (SeenSignatures.Count > maxSignatures)
            SeenSignatures.RemoveRange(0, SeenSignatures.Count - maxSignatures);
        if (SeenEventIds.Count > maxEventIds)
            SeenEventIds.RemoveRange(0, SeenEventIds.Count - maxEventIds);
    }
}

/// <summary>
/// Loads/saves <see cref="ErrorWatchState"/> in SQLite as a single row (Id = 1), with the two
/// bounded lists stored as JSON columns. Replaces the old JSON sidecar — so the watermark + dedup
/// memory now survive container redeploys. Best-effort: a failure logs and returns fresh state.
/// </summary>
public sealed class ErrorWatchStateStore(IDbContextFactory<ErdaDbContext> dbFactory, ILogger? logger = null)
{
    private const int RowId = 1;

    public ErrorWatchState Load()
    {
        try
        {
            using var db = dbFactory.CreateDbContext();
            var row = db.ErrorWatchState.AsNoTracking().FirstOrDefault(r => r.Id == RowId);
            if (row is null)
                return new ErrorWatchState();
            return new ErrorWatchState
            {
                LastTimestampUtc = row.LastTimestampUtc,
                SeenSignatures = JsonSerializer.Deserialize<List<string>>(row.SeenSignaturesJson) ?? [],
                SeenEventIds = JsonSerializer.Deserialize<List<string>>(row.SeenEventIdsJson) ?? [],
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load error-watch state; starting fresh.");
            return new ErrorWatchState();
        }
    }

    public void Save(ErrorWatchState state)
    {
        try
        {
            using var db = dbFactory.CreateDbContext();
            var row = db.ErrorWatchState.FirstOrDefault(r => r.Id == RowId);
            if (row is null)
            {
                row = new ErrorWatchRow { Id = RowId };
                db.ErrorWatchState.Add(row);
            }
            row.LastTimestampUtc = state.LastTimestampUtc;
            row.SeenSignaturesJson = JsonSerializer.Serialize(state.SeenSignatures);
            row.SeenEventIdsJson = JsonSerializer.Serialize(state.SeenEventIds);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not save error-watch state.");
        }
    }
}
