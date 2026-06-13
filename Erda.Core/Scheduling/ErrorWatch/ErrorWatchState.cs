using System.Text.Json;
using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Core.Scheduling;

/// <summary>Persisted watermark + dedup memory for the error-watch scheduler.</summary>
public sealed class ErrorWatchState
{
    /// <summary>Newest event timestamp processed so far; the next poll queries from here.</summary>
    public DateTimeOffset? LastTimestampUtc { get; set; }

    /// <summary>
    /// Signature → when it was last alerted on (bounded). A signature absent from the map is new;
    /// one present is suppressed until <c>ReAlertAfter</c> has elapsed since the recorded time.
    /// </summary>
    public Dictionary<string, DateTimeOffset> SignatureLastAlerted { get; set; } = new();

    /// <summary>Recently processed event ids (bounded), to skip boundary duplicates across polls.</summary>
    public List<string> SeenEventIds { get; set; } = [];

    /// <summary>Keep the bounded collections from growing without bound (drop the oldest first).</summary>
    public void Trim(int maxSignatures = 500, int maxEventIds = 500)
    {
        if (SignatureLastAlerted.Count > maxSignatures)
        {
            var stale = SignatureLastAlerted
                .OrderBy(kv => kv.Value)
                .Take(SignatureLastAlerted.Count - maxSignatures)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale)
                SignatureLastAlerted.Remove(key);
        }
        if (SeenEventIds.Count > maxEventIds)
            SeenEventIds.RemoveRange(0, SeenEventIds.Count - maxEventIds);
    }
}

/// <summary>
/// Loads/saves <see cref="ErrorWatchState"/> in SQLite as a single row (Id = 1), with the watermark,
/// the signature→last-alerted map, and the seen-event-id list stored as JSON columns. Replaces the
/// old JSON sidecar — so the watermark + dedup memory survive container redeploys. Best-effort: a
/// failure logs and returns fresh state.
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
                SignatureLastAlerted = LoadSignatures(row),
                SeenEventIds = JsonSerializer.Deserialize<List<string>>(row.SeenEventIdsJson) ?? [],
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load error-watch state; starting fresh.");
            return new ErrorWatchState();
        }
    }

    /// <summary>
    /// Reads the signature→last-alerted map, falling back to migrating a legacy
    /// <c>SeenSignaturesJson</c> list (pre-cooldown rows) — stamping each at the watermark so the
    /// cooldown starts fresh rather than replaying a burst on first deploy.
    /// </summary>
    private static Dictionary<string, DateTimeOffset> LoadSignatures(ErrorWatchRow row)
    {
        // A row migrated in by EF carries a blank/empty value for the new column — treat that (and any
        // empty map) as "nothing yet", which falls through to the legacy-list migration below.
        var map = string.IsNullOrWhiteSpace(row.SignatureLastAlertedJson)
            ? new()
            : JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(row.SignatureLastAlertedJson) ?? new();
        if (map.Count == 0)
        {
            var legacy = JsonSerializer.Deserialize<List<string>>(row.SeenSignaturesJson) ?? [];
            var stamp = row.LastTimestampUtc ?? DateTimeOffset.MinValue;
            foreach (var signature in legacy)
                map[signature] = stamp;
        }
        return map;
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
            row.SignatureLastAlertedJson = JsonSerializer.Serialize(state.SignatureLastAlerted);
            row.SeenSignaturesJson = "[]"; // superseded by SignatureLastAlertedJson; kept for backward-compat reads
            row.SeenEventIdsJson = JsonSerializer.Serialize(state.SeenEventIds);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not save error-watch state.");
        }
    }
}
