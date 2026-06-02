namespace Erda.Core.Data;

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
