using Erda.Agents.Tools;
using Erda.Core.Services.OnePassword;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// The secret-injection middleware swaps <c>op://…</c> string arguments for resolved values just
/// before the inner tool call, then restores the reference, so any telemetry layer that reads the
/// arguments only ever sees the reference — never the resolved secret.
/// </summary>
public class SecretInjectionTests
{
    private sealed class FakeResolver : IOpSecretResolver
    {
        public List<string> Resolved { get; } = [];
        public Task<string> ResolveAsync(string reference, CancellationToken ct = default)
        {
            Resolved.Add(reference);
            return Task.FromResult("REAL-SECRET-" + reference.Split('/').Last());
        }
    }

    private static FunctionInvocationContext Context(string toolName, params (string Key, object? Value)[] args)
    {
        var ctx = new FunctionInvocationContext { Function = AIFunctionFactory.Create(() => "ok", toolName) };
        foreach (var (k, v) in args) ctx.Arguments[k] = v;
        return ctx;
    }

    [Fact]
    public async Task Forwards_the_resolved_value_but_restores_the_reference_after()
    {
        var resolver = new FakeResolver();
        var middleware = SecretInjection.Middleware(resolver);
        var ctx = Context("browser_type", ("text", "op://Erda/Moxfield/password"), ("ref", "e7"));

        object? seenByInner = null;
        await middleware(null!, ctx,
            (c, ct) => { seenByInner = c.Arguments["text"]; return ValueTask.FromResult<object?>("typed"); },
            CancellationToken.None);

        Assert.Equal("REAL-SECRET-password", seenByInner);              // the tool got the real value
        Assert.Equal("op://Erda/Moxfield/password", ctx.Arguments["text"]); // …restored to the reference
        Assert.Equal("e7", ctx.Arguments["ref"]);                       // non-secret args untouched
        Assert.Equal(["op://Erda/Moxfield/password"], resolver.Resolved);
    }

    [Fact]
    public async Task Passes_through_untouched_when_no_reference_is_present()
    {
        var resolver = new FakeResolver();
        var middleware = SecretInjection.Middleware(resolver);
        var ctx = Context("browser_type", ("text", "hello world"));

        var nextCalled = false;
        var result = await middleware(null!, ctx,
            (c, ct) => { nextCalled = true; return ValueTask.FromResult<object?>("typed"); },
            CancellationToken.None);

        Assert.True(nextCalled);
        Assert.Equal("typed", result);
        Assert.Equal("hello world", ctx.Arguments["text"]);
        Assert.Empty(resolver.Resolved);                                // resolver never called
    }

    [Fact]
    public async Task Restores_the_reference_even_when_the_inner_call_throws()
    {
        var resolver = new FakeResolver();
        var middleware = SecretInjection.Middleware(resolver);
        var ctx = Context("browser_type", ("text", "op://Erda/Moxfield/password"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware(null!, ctx,
                (c, ct) => throw new InvalidOperationException("boom"),
                CancellationToken.None).AsTask());

        Assert.Equal("op://Erda/Moxfield/password", ctx.Arguments["text"]); // reference restored in finally
    }

    [Fact]
    public async Task Restores_all_references_when_a_later_resolve_throws()
    {
        // A resolver that succeeds for most refs but throws for the one ending in "/boom".
        var resolver = new ThrowingResolver();
        var middleware = SecretInjection.Middleware(resolver);
        var ctx = Context("browser_type",
            ("a", "op://Erda/A/password"),
            ("b", "op://Erda/B/boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware(null!, ctx, (c, ct) => ValueTask.FromResult<object?>("typed"), CancellationToken.None).AsTask());

        // Neither arg may be left holding a resolved secret — both restored to their references.
        Assert.Equal("op://Erda/A/password", ctx.Arguments["a"]);
        Assert.Equal("op://Erda/B/boom", ctx.Arguments["b"]);
    }

    private sealed class ThrowingResolver : IOpSecretResolver
    {
        public Task<string> ResolveAsync(string reference, CancellationToken ct = default) =>
            reference.EndsWith("/boom")
                ? throw new InvalidOperationException("op failed")
                : Task.FromResult("REAL-SECRET-" + reference.Split('/').Last());
    }
}
