using Erda.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Core.Scheduling;

/// <summary>Result of reading the reminders: valid reminders plus any rows whose <c>when</c> failed to parse.</summary>
public sealed record ReminderLoad(IReadOnlyList<Reminder> Reminders, IReadOnlyList<string> Malformed);

/// <summary>
/// Reads and writes reminder definitions in the SQLite database (the DB replacement for the old
/// vault-note table). The scheduler's run-state (last-fired / one-shot fired) lives on the same
/// rows but is owned by <see cref="ReminderStateStore"/>; this type only touches the definition
/// columns (kind / when / text / status). Parsing is tolerant — a row whose <c>when</c> won't parse
/// is reported in <see cref="ReminderLoad.Malformed"/> and skipped, never aborting the batch.
/// </summary>
public sealed class ReminderStore(IDbContextFactory<ErdaDbContext> dbFactory, ILogger<ReminderStore> logger)
{
    public ReminderLoad LoadAll()
    {
        using var db = dbFactory.CreateDbContext();
        var rows = db.Reminders.AsNoTracking().ToList();
        var reminders = new List<Reminder>();
        var malformed = new List<string>();
        foreach (var row in rows)
        {
            if (WhenSpec.TryParse(row.When, out var spec))
                reminders.Add(new Reminder(row.Id, row.Kind, row.When, row.Text, row.Status, spec!,
                    row.DirectToCodex, row.PreScript));
            else
                malformed.Add($"{row.Id} | {row.When} | {row.Text}");
        }
        if (malformed.Count > 0)
            logger.LogWarning("Reminders table has {Count} malformed row(s); skipped.", malformed.Count);
        return new ReminderLoad(reminders, malformed);
    }

    /// <summary>
    /// Insert a reminder (or update it in place if the id already exists), leaving it active. The
    /// <paramref name="directToCodex"/>/<paramref name="preScript"/> options are meaningful only for
    /// scheduled prompts and default to off, so callers that don't care (e.g. the agent's
    /// <c>schedule_prompt</c> tool) keep working unchanged and never plant a script.
    /// </summary>
    public void Append(ReminderKind kind, string id, string when, string text,
        bool directToCodex = false, string? preScript = null)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Reminders.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            db.Reminders.Add(new ReminderRow
            {
                Id = id, Kind = kind, When = when, Text = text, Status = ReminderStatus.Active,
                DirectToCodex = directToCodex, PreScript = preScript,
            });
        }
        else
        {
            row.Kind = kind;
            row.When = when;
            row.Text = text;
            row.Status = ReminderStatus.Active;
            row.DirectToCodex = directToCodex;
            row.PreScript = preScript;
        }
        db.SaveChanges();
    }

    /// <summary>
    /// Update only a row's definition columns (<c>When</c>, <c>Text</c>, <c>DirectToCodex</c>,
    /// <c>PreScript</c>) in place, leaving <c>Kind</c>, <c>Status</c> and the run-state
    /// (<c>LastFiredUtc</c>/<c>Fired</c>) untouched — unlike <see cref="Append"/>, which forces the
    /// row active. The id is stable, so the scheduler keeps tracking the same row. Returns false if
    /// no row matched.
    /// </summary>
    public bool Update(string id, string when, string text, bool directToCodex, string? preScript = null)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Reminders.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return false;
        row.When = when;
        row.Text = text;
        row.DirectToCodex = directToCodex;
        row.PreScript = preScript;
        db.SaveChanges();
        return true;
    }

    /// <summary>Set a row's lifecycle status by id. Returns false if no row matched.</summary>
    public bool SetStatus(string id, ReminderStatus status)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Reminders.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return false;
        row.Status = status;
        db.SaveChanges();
        return true;
    }

    /// <summary>Delete a row by id. Returns false if no row matched.</summary>
    public bool Remove(string id)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Reminders.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return false;
        db.Reminders.Remove(row);
        db.SaveChanges();
        return true;
    }
}
