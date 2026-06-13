namespace Erda.Core.Data;

/// <summary>
/// Single-row (<see cref="Id"/> = 1) persisted state for the error-watch scheduler: the watermark,
/// the signature→last-alerted map, and the seen-event-id list, stored as JSON columns. Replaces the
/// JSON sidecar file. <see cref="SeenSignaturesJson"/> is the legacy pre-cooldown column, retained so
/// existing rows migrate on load; new writes go to <see cref="SignatureLastAlertedJson"/>.
/// </summary>
public sealed class ErrorWatchRow
{
    public int Id { get; set; }
    public DateTimeOffset? LastTimestampUtc { get; set; }
    public string SeenSignaturesJson { get; set; } = "[]";
    public string SignatureLastAlertedJson { get; set; } = "{}";
    public string SeenEventIdsJson { get; set; } = "[]";
}
