using Erda.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Erda.Api;

/// <summary>
/// Wires the control-panel JSON API: cookie authentication, the <see cref="ConfigPanelService"/>, and
/// the <c>/api/*</c> endpoint groups guarded by the <see cref="CsrfEndpointFilter"/>. Replaces the
/// Blazor Server presentation; the SPA (built by Vite) is served as static files with an
/// <c>index.html</c> fallback configured in <c>Program.cs</c>.
/// </summary>
public static class PanelApi
{
    /// <summary>Register panel services + cookie auth. Call before <c>builder.Build()</c>.</summary>
    public static IServiceCollection AddPanelApi(this IServiceCollection services)
    {
        services.AddSingleton<ConfigPanelService>();

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "erda_panel";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;        // blocks cookie on cross-site mutations
                options.Cookie.SecurePolicy = CookieSecurePolicy.None; // panel is plain-HTTP on the LAN
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                // The API speaks status codes, never HTML redirects to a login page.
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Map <c>/api/*</c>. Auth endpoints (login/logout/me) are always open; the data endpoints
    /// require an authenticated cookie only when a <c>Panel:Password</c> is configured. Every group
    /// carries the CSRF header filter.
    /// </summary>
    public static void MapPanelApi(this WebApplication app)
    {
        var authRequired = app.Services.GetRequiredService<IOptions<PanelOptions>>().Value.AuthRequired;

        var open = app.MapGroup("/api").AddEndpointFilter<CsrfEndpointFilter>();
        open.MapAuthEndpoints();

        var data = app.MapGroup("/api").AddEndpointFilter<CsrfEndpointFilter>();
        if (authRequired)
            data.RequireAuthorization();

        data.MapReminderEndpoints();
        data.MapPromptEndpoints();
        data.MapActivityEndpoints();
        data.MapConfigEndpoints();
    }
}
