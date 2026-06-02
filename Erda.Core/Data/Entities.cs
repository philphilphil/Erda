using Erda.Core.Scheduling;

namespace Erda.Core.Data;

/// <summary>
/// A saved version of the system prompt. Exactly one row has <see cref="IsActive"/> = true; that
/// is the prompt the agent is built from at startup. Saving a new version inserts a row and moves
/// the active flag; older rows are kept for diff / rollback.
/// </summary>
public sealed class PromptVersion
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public string? Note { get; set; }
}

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

/// <summary>
/// Single-row (<see cref="Id"/> = 1) persisted state for the error-watch scheduler: the watermark
/// plus the two bounded dedup lists, stored as JSON columns. Replaces the JSON sidecar file.
/// </summary>
public sealed class ErrorWatchRow
{
    public int Id { get; set; }
    public DateTimeOffset? LastTimestampUtc { get; set; }
    public string SeenSignaturesJson { get; set; } = "[]";
    public string SeenEventIdsJson { get; set; } = "[]";
}

/// <summary>
/// One entry in the panel's activity feed. Append-only; pruned to the most recent
/// <c>Panel:ActivityRetention</c> rows. Seq remains the durable, full-fidelity record.
/// </summary>
public sealed class ActivityEntry
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>One of: agent_run, tool_call, scheduled_fire, error_alert.</summary>
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? DetailJson { get; set; }
}

/// <summary>
/// A configuration override edited in the panel. <see cref="Key"/> is in ASP.NET
/// <c>Section:Key</c> form (e.g. <c>ErrorWatch:MinLevel</c>); loaded at startup by the SQLite
/// configuration provider, layered over appsettings/env. Applied on restart (v1).
/// </summary>
public sealed class ConfigOverride
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}
