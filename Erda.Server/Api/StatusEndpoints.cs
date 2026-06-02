using System.Diagnostics;

namespace Erda.Server.Api;

/// <summary>Agent liveness for the panel's sidebar footer: if this responds the agent is serving, so
/// <c>Online</c> is always true; <c>StartedAtUtc</c> is the process start time, which the SPA renders
/// as uptime.</summary>
public sealed record StatusResponse(bool Online, DateTimeOffset StartedAtUtc);

/// <summary>The <c>/api/status</c> endpoint backing the sidebar's "Agent online · uptime …" footer.</summary>
public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusEndpoints(this RouteGroupBuilder group)
    {
        // Captured once at startup — the process start time is stable for the life of the host.
        var startedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        group.MapGet("/status", () => Results.Ok(new StatusResponse(true, startedAtUtc)));
        return group;
    }
}
