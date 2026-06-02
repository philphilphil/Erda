using System.Security.Claims;
using Erda.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Erda.Api;

/// <summary>
/// Cookie-auth endpoints for the panel. Auth is off by default: when <see cref="PanelOptions.Password"/>
/// is blank the panel is open (login is a no-op and <c>me</c> reports not-required). These endpoints
/// are always reachable, even when the rest of the API requires a cookie.
/// </summary>
public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/auth");

        g.MapGet("/me", (HttpContext http, IOptions<PanelOptions> opts) =>
        {
            var authRequired = opts.Value.AuthRequired;
            // When auth is off the panel is open to everyone, so report authenticated:true.
            var authenticated = !authRequired || (http.User.Identity?.IsAuthenticated ?? false);
            return Results.Ok(new AuthState(authRequired, authenticated));
        });

        g.MapPost("/login", async (LoginRequest req, HttpContext http, IOptions<PanelOptions> opts) =>
        {
            var panel = opts.Value;
            if (!panel.AuthRequired)
                return Results.Ok(); // open panel — nothing to authenticate against

            if (!PanelCredentials.IsValid(panel, req.Username, req.Password))
                return Results.Unauthorized();

            var name = string.IsNullOrWhiteSpace(panel.Username) ? "panel" : panel.Username;
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, name)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });
            return Results.Ok();
        });

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        return group;
    }
}
