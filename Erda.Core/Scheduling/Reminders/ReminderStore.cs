using Erda.Data;
using Microsoft.EntityFrameworkCore;

namespace Erda.Scheduling;

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
                reminders.Add(new Reminder(row.Id, row.Kind, row.When, row.Text, row.Status, spec!));
            else
                malformed.Add($"{row.Id} | {row.When} | {row.Text}");
        }
        if (malformed.Count > 0)
            logger.LogWarning("Reminders table has {Count} malformed row(s); skipped.", malformed.Count);
        return new ReminderLoad(reminders, malformed);
    }

    /// <summary>Insert a reminder (or update it in place if the id already exists), leaving it active.</summary>
    public void Append(ReminderKind kind, string id, string when, string text)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Reminders.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            db.Reminders.Add(new ReminderRow
            {
                Id = id, Kind = kind, When = when, Text = text, Status = ReminderStatus.Active,
            });
        }
        else
        {
            row.Kind = kind;
            row.When = when;
            row.Text = text;
            row.Status = ReminderStatus.Active;
        }
        db.SaveChanges();
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
