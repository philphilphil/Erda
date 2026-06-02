namespace Erda.Server.Api;

/// <summary>One activity-feed entry for display.</summary>
public sealed record ActivityDto(long Id, DateTimeOffset TimestampUtc, string Kind, string Summary);
