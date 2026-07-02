namespace Erda.Server.Api;

/// <summary>One activity-feed entry for display. <see cref="Detail"/> is the raw structured
/// payload (JSON) recorded with the event, e.g. a tool call's arguments — panel-only, never
/// shipped to Seq. Null when the event carried no detail.</summary>
public sealed record ActivityDto(long Id, DateTimeOffset TimestampUtc, string Kind, string Summary, string? Detail);
