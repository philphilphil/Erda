namespace Erda.Server.Api;

/// <summary>
/// JSON endpoints over <see cref="ConfigPanelService"/> for the panel's read-only Config screen, plus
/// the one-click restart. Config is env-only and applied at startup — there is no edit endpoint;
/// change <c>.env</c> and restart.
/// </summary>
public static class ConfigEndpoints
{
    public static RouteGroupBuilder MapConfigEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/config");

        g.MapGet("", (ConfigPanelService svc) => Results.Ok(svc.GetItems()));

        g.MapPost("/restart", (IHostApplicationLifetime lifetime, ILogger<ConfigPanelService> log) =>
        {
            log.LogInformation("Restart requested from control panel; stopping application.");
            // Stop just after the response flushes so the SPA gets its 200 before the socket drops.
            // In Docker (restart: unless-stopped) the container comes back; under `dotnet run` it exits.
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                lifetime.StopApplication();
            });
            return Results.Ok();
        });

        return group;
    }
}
