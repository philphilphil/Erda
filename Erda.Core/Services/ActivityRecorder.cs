using System.Text.Json;
using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Core.Services;

/// <summary>
/// Records discrete activity events (agent runs, tool calls, scheduled fires, error alerts)
/// to the SQLite store and pushes them to live subscribers for UI display.
/// </summary>
public interface IActivityRecorder
{
    /// <summary>Persist one activity entry (best-effort — never throws) and notify live subscribers.</summary>
    /// <param name="kind">Event category, e.g. <c>agent_run</c>, <c>tool_call</c>, <c>scheduled_fire</c>, <c>error_alert</c>.</param>
    /// <param name="summary">Short human-readable description of the event.</param>
    /// <param name="detail">Optional structured payload; serialized to JSON and stored in <see cref="ActivityEntry.DetailJson"/>.</param>
    void Record(string kind, string summary, object? detail = null);

    /// <summary>Most recent entries, newest first (default 100, capped).</summary>
    /// <param name="max">Maximum number of entries to return; clamped to the range 1..1000.</param>
    IReadOnlyList<ActivityEntry> Recent(int max = 100);

    /// <summary>Raised after an entry is persisted, for live UI push (Blazor Server).</summary>
    event Action<ActivityEntry>? Recorded;
}

/// <summary>
/// Default <see cref="IActivityRecorder"/> backed by <see cref="ErdaDbContext"/> via an
/// <see cref="IDbContextFactory{TContext}"/>. Each operation opens a short-lived context,
/// matching the singleton / background-service consumers. Recording is best-effort: failures
/// are logged and swallowed so telemetry can never break a caller (agent turn / scheduler tick).
/// </summary>
public sealed class ActivityRecorder(
    IDbContextFactory<ErdaDbContext> dbFactory,
    ILogger<ActivityRecorder> logger) : IActivityRecorder
{
    /// <summary>Upper bound on retained rows; older entries are pruned after each successful insert.</summary>
    private const int MaxRetained = 1000;

    /// <inheritdoc />
    public event Action<ActivityEntry>? Recorded;

    /// <inheritdoc />
    public void Record(string kind, string summary, object? detail = null)
    {
        try
        {
            var entry = new ActivityEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Kind = kind,
                Summary = summary,
                DetailJson = detail is null ? null : JsonSerializer.Serialize(detail),
            };

            using var db = dbFactory.CreateDbContext();
            db.Activity.Add(entry);
            db.SaveChanges();

            // Prune anything beyond the newest MaxRetained rows. Find the Id of the
            // MaxRetained-th newest entry and delete everything older than it.
            var cutoffId = db.Activity
                .OrderByDescending(a => a.Id)
                .Select(a => a.Id)
                .Skip(MaxRetained - 1)
                .FirstOrDefault();

            if (cutoffId > 0)
                db.Activity.Where(a => a.Id < cutoffId).ExecuteDelete();

            Recorded?.Invoke(entry);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record activity entry (kind={Kind}); ignoring.", kind);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityEntry> Recent(int max = 100)
    {
        using var db = dbFactory.CreateDbContext();
        return db.Activity
            .OrderByDescending(a => a.Id)
            .Take(Math.Clamp(max, 1, MaxRetained))
            .ToList();
    }
}
