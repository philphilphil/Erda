using Erda.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// The CSRF guard requires <c>X-Requested-With: erda-panel</c> on mutating verbs and is a no-op on
/// safe verbs (so GET/SSE pass through unchanged).
/// </summary>
public class CsrfEndpointFilterTests
{
    private static readonly object Sentinel = new();

    private static async Task<(object? result, bool nextCalled)> Invoke(string method, string? header)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (header is not null)
            http.Request.Headers["X-Requested-With"] = header;

        var ctx = EndpointFilterInvocationContext.Create(http);
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(Sentinel);
        };

        var result = await new CsrfEndpointFilter().InvokeAsync(ctx, next);
        return (result, nextCalled);
    }

    [Fact]
    public async Task Mutation_without_header_is_forbidden()
    {
        var (result, nextCalled) = await Invoke("POST", header: null);
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Mutation_with_wrong_header_is_forbidden()
    {
        var (result, nextCalled) = await Invoke("DELETE", header: "nope");
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Mutation_with_correct_header_passes_through()
    {
        var (result, nextCalled) = await Invoke("PUT", header: "erda-panel");
        Assert.True(nextCalled);
        Assert.Same(Sentinel, result);
    }

    [Fact]
    public async Task Get_is_exempt_even_without_the_header()
    {
        var (result, nextCalled) = await Invoke("GET", header: null);
        Assert.True(nextCalled);
        Assert.Same(Sentinel, result);
    }
}
