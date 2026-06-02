namespace Erda.Core.Data;

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
