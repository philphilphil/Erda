namespace Erda.Api;

/// <summary>
/// Pragmatic CSRF guard for the cookie-authenticated panel API on a plain-HTTP LAN. Cookies are
/// <c>SameSite=Lax</c>, which already blocks them on cross-site state-changing requests; as
/// belt-and-suspenders this filter additionally requires the custom header
/// <c>X-Requested-With: erda-panel</c> on every mutating verb (POST/PUT/DELETE/PATCH). A cross-origin
/// "simple" request cannot set a custom header without a CORS preflight (which the server does not
/// grant), so only the SPA — which always sends it — can mutate. Safe verbs (GET/HEAD/OPTIONS),
/// including the SSE stream, are exempt.
/// </summary>
public sealed class CsrfEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Requested-With";
    public const string ExpectedValue = "erda-panel";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!SafeMethods.Contains(request.Method))
        {
            var header = request.Headers[HeaderName].ToString();
            if (!string.Equals(header, ExpectedValue, StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
