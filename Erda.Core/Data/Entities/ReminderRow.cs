using Erda.Core.Scheduling;

namespace Erda.Core.Data;

/// <summary>
/// A reminder or scheduled prompt (the DB replacement for the old vault-note table row). Holds the
/// definition plus the run-state that used to live in the JSON sidecar. <see cref="When"/> keeps
/// the same syntax as before (datetime <c>yyyy-MM-dd HH:mm</c> or a cron expression) and is parsed
/// to a <c>WhenSpec</c> at load — it is not stored parsed.
/// </summary>
public sealed class ReminderRow
{
    public string Id { get; set; } = "";
    public ReminderKind Kind { get; set; }
    public string When { get; set; } = "";
    public string Text { get; set; } = "";
    public ReminderStatus Status { get; set; }

    /// <summary>Recurring cadence: the last instant this row fired (was <c>LastFiredUtc[id]</c>).</summary>
    public DateTimeOffset? LastFiredUtc { get; set; }

    /// <summary>One-shot send-once backstop (was membership in <c>FiredOneShotIds</c>).</summary>
    public bool Fired { get; set; }
}
