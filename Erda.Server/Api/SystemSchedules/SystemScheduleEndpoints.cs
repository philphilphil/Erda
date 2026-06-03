using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Server.Api;

/// <summary>
/// Read-only listing of background schedulers the system runs on its own (currently just the Seq
/// error watch). Hand-curated: each entry is built from its scheduler's live options, so it tracks
/// config (e.g. <c>ErrorWatch:Enabled</c>) the same way the rest of the app does — applied on
/// restart. No mutations; the panel renders this as the "System scheduled" area.
/// </summary>
public static class SystemScheduleEndpoints
{
    public static RouteGroupBuilder MapSystemScheduleEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/system-schedules");

        g.MapGet("", (IOptions<ErrorWatchOptions> errorWatch) =>
        {
            var schedules = new List<SystemScheduleDto> { DescribeErrorWatch(errorWatch.Value) };
            return Results.Ok(new SystemSchedulesResponse(schedules));
        });

        return group;
    }

    private static SystemScheduleDto DescribeErrorWatch(ErrorWatchOptions o) => new(
        Key: "errorwatch",
        Name: "Error watch",
        Icon: "alert",
        Description: "Polls Seq for new errors, diagnoses each with Codex, and alerts you on WhatsApp.",
        Enabled: o.Enabled,
        Status: o.Enabled ? "Running" : "Disabled",
        Tags:
        [
            $"every {FormatInterval(o.PollInterval)}",
            $"min level {o.MinLevel}",
            $"≤{o.MaxAlertsPerPoll}/poll",
        ]);

    /// <summary>Render a poll interval as a short human string: "30 s", "15 min", "1 h", "2 h 30 min".</summary>
    private static string FormatInterval(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min";
        if (t.Minutes == 0) return $"{(int)t.TotalHours} h";
        return $"{(int)t.TotalHours} h {t.Minutes} min";
    }
}
