namespace Erda.Server.Api;

/// <summary>
/// Read-only descriptor of a background scheduler the system runs on its own (e.g. the Seq error
/// watch). Surfaced in the panel's "System scheduled" area; not editable. <see cref="Status"/> is a
/// display label ("Running" / "Disabled") derived from <see cref="Enabled"/>; <see cref="Tags"/> are
/// pre-formatted cadence chips the SPA renders verbatim.
/// </summary>
public sealed record SystemScheduleDto(
    string Key,
    string Name,
    string Icon,
    string Description,
    bool Enabled,
    string Status,
    IReadOnlyList<string> Tags);

public sealed record SystemSchedulesResponse(IReadOnlyList<SystemScheduleDto> Schedules);
