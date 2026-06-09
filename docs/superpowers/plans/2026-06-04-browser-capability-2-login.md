# Browser Capability — Plan 2: 1Password Login (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the browser sub-agent log itself into sites using credentials it never sees: a `find_login(domain)` tool that maps the page's registrable domain to a 1Password item and returns `op://…` **references** (never values), plus a secret-injection middleware that resolves those references at type-time (including TOTP) and scrubs the real value from telemetry. The 1Password `Erda` vault is the account registry — no DB table.

**Architecture:** A single subprocess seam, `IOpCli`/`OpCli` (Core), shells out to the `op` CLI under a read-only service-account token (mirrors `PreScriptRunner`). `IOpSecretResolver`/`OpSecretResolver` (Core) turns one `op://…` reference into a value via `op read` (or, for TOTP, `op item get --otp`, never cached). `FindLogin` (Agents) is the `find_login(domain)` tool: `op item list` → match registrable domain → `op item get` → return references. `SecretInjection` (Agents) is a function-invocation middleware on the **browser sub-agent only** that swaps `op://…` string args for resolved values just before the MCP `type` call, restoring the reference afterward so OpenTelemetry only ever records the reference. The `find_login` tool and the middleware are wired into the existing `BrowserAgent.TryCreateTool` pipeline. The Capabilities page gains a read-only "Logins Erda can use" list from `op item list` (titles + sites only).

**Tech Stack:** .NET 10, Microsoft Agent Framework (MAF) 1.8.0, `Microsoft.Extensions.AI` 10.6.0 function-invocation middleware, the 1Password `op` CLI (subprocess), Vue 3 + Vite SPA.

**Spec:** [`../specs/2026-06-04-erda-browser-capability-design.md`](../specs/2026-06-04-erda-browser-capability-design.md) — this plan covers Components 4, 5, 6, 7 (and the optional read-only accounts part of Component 9). Component 8 (screenshots → WhatsApp) is Plan 3.

**Scope boundary:** This plan ends at "the browser sub-agent can find a login by domain, fill a form using `op://…` references that resolve below the LLM (incl. TOTP), and the password never appears in the transcript, the activity feed, or a Seq span." Captcha / push-or-SMS 2FA is explicitly a hard stop (the sub-agent reports it is blocked; the orchestrator's existing `message_me` tool pings Phil). No first-login manual-capture path is required — agent-driven login is the normal path; the README documents the manual fallback.

---

## Background facts (verified against the current branch — rely on these)

- **`BrowserAgent.TryCreateTool`** (`Erda.Agents/Orchestration/BrowserAgent.cs`) already builds the browser sub-agent as `chat.AsAIAgent(instructions, name:"browser", tools:[.. mcp.Tools]).AsBuilder().UseOpenTelemetry(...).Build()`. This plan adds `find_login` to the `tools` array and `.Use(SecretInjection.Middleware(resolver))` **after** `UseOpenTelemetry` (so OTel is the outer layer and records the pre-mutation reference).
- **Middleware shape** (copy from `Erda.Agents/Orchestration/ToolCallActivity.cs`): a function-invocation middleware is a `Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>>`. `FunctionInvocationContext.Function` is the `AIFunction`; `FunctionInvocationContext.Arguments` is a mutable `AIFunctionArguments` (an `IDictionary<string, object?>`). Tests construct it directly: `new FunctionInvocationContext { Function = fn }` (see `Erda.Tests/ToolCallActivityTests.cs`).
- **Subprocess pattern** to mirror: `Erda.Core/Services/PreScriptRunner.cs` (fresh temp dir, `ArgumentList`, closed stdin → EOF, bounded timeout via `CancellationTokenSource`, process-tree kill, content-gated logging). `OpCli` follows it. Unlike `CodexRunner`, **do not strip any env var** — `op` reads `OP_SERVICE_ACCOUNT_TOKEN` from the inherited environment.
- **Config:** `BrowserOptions` (`Erda.Core/Configuration/BrowserOptions.cs`, section `Erda:Browser`) is already bound in `Erda.Core/ServiceCollectionExtensions.cs`. This plan adds `OpCommand` and `OnePasswordVault` to it.
- **Capabilities group** already exists: `Erda.Server/Api/Capabilities/{CapabilitiesEndpoints,CapabilitiesDtos}.cs`, mapped in `PanelApi`. The SPA client helper pattern is `get<T>('/api/...')` in `web/src/api/client.ts`; DTOs in `web/src/api/types.ts`; the view is `web/src/views/CapabilitiesView.vue`.
- **Test commands:** `dotnet build Erda.slnx`; `dotnet test Erda.Tests/Erda.Tests.csproj`; filter syntax `--filter "FullyQualifiedName~ClassName"`. SPA: `cd web && npm run build`. Keep the suite green (200 tests now).
- The harness may show stale LSP `CS0246` errors after cross-project edits — they are false; trust `dotnet build`.

---

## File Structure

**Create (Core):**
- `Erda.Core/Services/OnePassword/IOpCli.cs` — the one subprocess seam: run `op` with an argument list, return stdout, throw on failure.
- `Erda.Core/Services/OnePassword/OpCli.cs` — real impl (mirrors `PreScriptRunner`). Manual-verified (needs the binary).
- `Erda.Core/Services/OnePassword/IOpSecretResolver.cs` — `ResolveAsync(reference)` → value.
- `Erda.Core/Services/OnePassword/OpSecretResolver.cs` — `op read` / `op item get --otp`; vault-scoped; never caches TOTP; never logs values.

**Create (Agents):**
- `Erda.Agents/Tools/RegistrableDomain.cs` — pure eTLD+1 helper for domain matching.
- `Erda.Agents/Tools/FindLogin.cs` — pure parsers + matching + reference building + the `find_login` `AIFunction` factory.
- `Erda.Agents/Tools/SecretInjection.cs` — the function-invocation middleware.

**Modify:**
- `Erda.Core/Configuration/BrowserOptions.cs` — add `OpCommand`, `OnePasswordVault`.
- `Erda.Core/ServiceCollectionExtensions.cs` — register `IOpCli`/`IOpSecretResolver`.
- `Erda.Agents/Orchestration/BrowserAgent.cs` — add `find_login` to the tool list, add `.Use(SecretInjection.Middleware(...))`, extend the system prompt with the login playbook.
- `Erda.Server/Api/Capabilities/CapabilitiesDtos.cs` + `CapabilitiesEndpoints.cs` — `GET /api/capabilities/accounts` (read-only titles + sites).
- `web/src/api/types.ts`, `web/src/api/client.ts`, `web/src/views/CapabilitiesView.vue` — "Logins Erda can use" card.
- `Dockerfile` — add the `op` binary to the runtime stage.
- `docker-compose.yml`, `.env.example` — `OP_SERVICE_ACCOUNT_TOKEN` passthrough.
- `README.md` — 1Password service-account setup + manual session fallback.

**Test:**
- `Erda.Tests/OpSecretResolverTests.cs`
- `Erda.Tests/SecretInjectionTests.cs`
- `Erda.Tests/RegistrableDomainTests.cs`
- `Erda.Tests/FindLoginTests.cs`
- `Erda.Tests/CapabilitiesEndpointTests.cs` (extend — accounts mapping)

> **Testability note:** the real `OpCli` launches `op` and is **manual-verified** (Task 8), exactly as `PlaywrightMcp` was in Plan 1. Everything that *consumes* it — the resolver's arg-building, the middleware, domain matching, JSON parsing, reference building, the accounts DTO mapping — is unit-tested against a **fake `IOpCli`** / direct construction. We test our logic, not the SDK or the `op` binary.

---

## Task 1: BrowserOptions config + `IOpCli`/`OpCli` subprocess seam + DI

**Files:**
- Modify: `Erda.Core/Configuration/BrowserOptions.cs`
- Create: `Erda.Core/Services/OnePassword/IOpCli.cs`
- Create: `Erda.Core/Services/OnePassword/OpCli.cs`
- Modify: `Erda.Core/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add the two config keys**

In `Erda.Core/Configuration/BrowserOptions.cs`, after the `MaxSteps` property (before the closing brace), add:
```csharp

    /// <summary>Executable that runs the 1Password CLI (resolves <c>op://…</c> references and lists
    /// the <see cref="OnePasswordVault"/> items). On PATH in the runtime image.</summary>
    public string OpCommand { get; set; } = "op";

    /// <summary>The single 1Password vault Erda may read. It is the account registry AND the
    /// allow-list: only logins in this vault can be used. The service-account token is scoped
    /// read-only to it. References outside this vault are refused by the resolver.</summary>
    public string OnePasswordVault { get; set; } = "Erda";
```

- [ ] **Step 2: Define the subprocess seam**

Create `Erda.Core/Services/OnePassword/IOpCli.cs`:
```csharp
namespace Erda.Core.Services.OnePassword;

/// <summary>
/// The single seam to the 1Password <c>op</c> CLI. Runs <c>op</c> with a verbatim argument list
/// (no shell) and returns its stdout, throwing <see cref="OpCliException"/> on a non-zero exit.
/// Everything that interprets <c>op</c> output (the secret resolver, the login lookup, the accounts
/// panel) depends on this interface so it can be unit-tested with a fake — only the real
/// <see cref="OpCli"/> needs the binary.
/// </summary>
public interface IOpCli
{
    /// <summary>Run <c>op</c> with <paramref name="args"/>; returns trimmed stdout. Throws
    /// <see cref="OpCliException"/> if the process exits non-zero or cannot be launched.</summary>
    Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when an <c>op</c> invocation fails. The message carries the exit code and a
/// trimmed tail of stderr — never a resolved secret value.</summary>
public sealed class OpCliException(string message) : Exception(message);
```

- [ ] **Step 3: Implement `OpCli` (mirrors `PreScriptRunner`)**

Create `Erda.Core/Services/OnePassword/OpCli.cs`:
```csharp
using System.Diagnostics;
using System.Text;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services.OnePassword;

/// <summary>
/// Real <see cref="IOpCli"/>: shells out to the <c>op</c> CLI. Mirrors <see cref="Erda.Core.Services.PreScriptRunner"/>'s
/// subprocess handling (no shell, closed stdin → EOF, bounded timeout, process-tree kill). The
/// environment is inherited unchanged so <c>op</c> picks up <c>OP_SERVICE_ACCOUNT_TOKEN</c>.
///
/// Logging is deliberately minimal and value-free: it logs the argv (which only ever contains
/// references / item ids / flags — never secret values) and timing, never stdout.
/// </summary>
public sealed class OpCli(IOptions<BrowserOptions> options, ILogger<OpCli> logger) : IOpCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.Value.OpCommand,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new OpCliException(
                $"Failed to launch the '{options.Value.OpCommand}' CLI. Ensure the 1Password CLI is installed and on PATH. ({ex.Message})");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close(); // EOF — op never reads stdin here

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new OpCliException($"op {args[0]} exceeded {Timeout} and was killed.");
        }

        if (proc.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            var tail = err.Length > 300 ? "…" + err[^300..] : err;
            // argv is safe to log (references/ids/flags only). stderr from op does not echo values.
            logger.LogWarning("op {Args} failed (exit {Exit}) in {Ms}ms: {Err}",
                string.Join(' ', args), proc.ExitCode, sw.ElapsedMilliseconds, tail);
            throw new OpCliException($"op {args[0]} failed (exit {proc.ExitCode}): {tail}");
        }

        logger.LogDebug("op {Args} ok in {Ms}ms", string.Join(' ', args), sw.ElapsedMilliseconds);
        return stdout.ToString().Trim();
    }
}
```
> No test for `OpCli` — it requires the binary (verified in Task 8). It is a thin, logic-free subprocess wrapper, exactly like `PlaywrightMcp` in Plan 1.

- [ ] **Step 4: Register in Core DI**

In `Erda.Core/ServiceCollectionExtensions.cs`, in the `--- Shared services ---` block (right after the `PreScriptRunner` registrations, ~line 45), add:
```csharp
        // --- 1Password (op CLI) — secret resolution + login lookup for the browser sub-agent ---
        services.AddSingleton<Erda.Core.Services.OnePassword.IOpCli, Erda.Core.Services.OnePassword.OpCli>();
        services.AddSingleton<Erda.Core.Services.OnePassword.IOpSecretResolver, Erda.Core.Services.OnePassword.OpSecretResolver>();
```
> `OpSecretResolver` doesn't exist yet (Task 2). It is referenced here so the registration is complete in one place; the build in this task will fail until Task 2 adds the type. If you prefer a green build at the end of Task 1, add only the `IOpCli` line now and the resolver line in Task 2 Step 5. Either is fine — the plan assumes you add the resolver line in Task 2.

For a green Task-1 build, add **only** this line now:
```csharp
        // --- 1Password (op CLI) — subprocess seam (resolver registered in the next task) ---
        services.AddSingleton<Erda.Core.Services.OnePassword.IOpCli, Erda.Core.Services.OnePassword.OpCli>();
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build Erda.slnx`
Expected: build succeeds.

- [ ] **Step 6: Commit**
```bash
git add Erda.Core/Configuration/BrowserOptions.cs Erda.Core/Services/OnePassword/IOpCli.cs Erda.Core/Services/OnePassword/OpCli.cs Erda.Core/ServiceCollectionExtensions.cs
git commit -m "feat(browser): op CLI subprocess seam (IOpCli/OpCli) + vault config"
```

---

## Task 2: `IOpSecretResolver`/`OpSecretResolver` (TDD)

**Files:**
- Create: `Erda.Core/Services/OnePassword/IOpSecretResolver.cs`
- Create: `Erda.Core/Services/OnePassword/OpSecretResolver.cs`
- Modify: `Erda.Core/ServiceCollectionExtensions.cs`
- Test: `Erda.Tests/OpSecretResolverTests.cs`

- [ ] **Step 1: Define the interface**

Create `Erda.Core/Services/OnePassword/IOpSecretResolver.cs`:
```csharp
namespace Erda.Core.Services.OnePassword;

/// <summary>
/// Resolves a single 1Password secret <b>reference</b> (<c>op://Vault/Item/field</c>) to its
/// current value. Plain fields resolve via <c>op read</c>; a one-time-password field resolves to the
/// current 6-digit TOTP code via <c>op item get --otp</c> and is <b>never cached</b> (codes rotate).
/// Only references inside the configured vault are accepted. The resolved value is never logged.
/// </summary>
public interface IOpSecretResolver
{
    Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Write the failing tests**

Create `Erda.Tests/OpSecretResolverTests.cs`:
```csharp
using Erda.Core.Configuration;
using Erda.Core.Services.OnePassword;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class OpSecretResolverTests
{
    /// <summary>Fake op CLI: records the argv it was called with and returns a canned stdout.</summary>
    private sealed class FakeOpCli(string stdout) : IOpCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args);
            return Task.FromResult(stdout);
        }
    }

    private static OpSecretResolver Make(IOpCli cli, string vault = "Erda") =>
        new(cli, Options.Create(new BrowserOptions { OnePasswordVault = vault }), NullLogger<OpSecretResolver>.Instance);

    [Fact]
    public async Task Resolves_a_plain_field_via_op_read()
    {
        var cli = new FakeOpCli("s3cr3t-password");
        var value = await Make(cli).ResolveAsync("op://Erda/Moxfield/password");

        Assert.Equal("s3cr3t-password", value);
        var argv = Assert.Single(cli.Calls);
        Assert.Equal(["read", "op://Erda/Moxfield/password"], argv);
    }

    [Fact]
    public async Task Resolves_a_totp_field_via_op_item_get_otp()
    {
        var cli = new FakeOpCli("123456");
        var value = await Make(cli).ResolveAsync("op://Erda/Moxfield/one-time password");

        Assert.Equal("123456", value);
        var argv = Assert.Single(cli.Calls);
        Assert.Equal(["item", "get", "Moxfield", "--vault", "Erda", "--otp"], argv);
    }

    [Fact]
    public async Task Refuses_a_reference_outside_the_configured_vault()
    {
        var cli = new FakeOpCli("should-not-be-read");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Make(cli).ResolveAsync("op://Personal/Bank/password"));

        Assert.Empty(cli.Calls);                          // never shelled out
        Assert.DoesNotContain("should-not-be-read", ex.Message);
    }

    [Theory]
    [InlineData("not-a-reference")]
    [InlineData("op://Erda/Moxfield")]                     // missing field
    public async Task Rejects_malformed_references(string reference)
    {
        var cli = new FakeOpCli("x");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Make(cli).ResolveAsync(reference));
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Throws_when_op_returns_an_empty_value()
    {
        var cli = new FakeOpCli("   ");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Make(cli).ResolveAsync("op://Erda/Moxfield/username"));
        Assert.Contains("op://Erda/Moxfield/username", ex.Message);   // names the ref, carries no value
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~OpSecretResolverTests"`
Expected: FAIL — `OpSecretResolver` does not exist.

- [ ] **Step 4: Implement `OpSecretResolver`**

Create `Erda.Core/Services/OnePassword/OpSecretResolver.cs`:
```csharp
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Core.Services.OnePassword;

/// <inheritdoc />
public sealed class OpSecretResolver(IOpCli cli, IOptions<BrowserOptions> options, ILogger<OpSecretResolver> logger)
    : IOpSecretResolver
{
    private readonly string _vault = options.Value.OnePasswordVault;

    public async Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        var (vault, item, field) = Parse(reference);

        // Defense in depth: the service-account token is already scoped to one vault, but refuse any
        // reference that names a different vault so a prompt-injected ref can't even be attempted.
        if (!string.Equals(vault, _vault, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing 1Password reference outside the '{_vault}' vault.");

        // TOTP must be re-resolved every use (codes rotate every 30s) and is fetched with the
        // dedicated --otp flag, which returns just the current 6-digit code.
        string value = IsOneTimePassword(field)
            ? await cli.RunAsync(["item", "get", item, "--vault", _vault, "--otp"], cancellationToken)
            : await cli.RunAsync(["read", reference], cancellationToken);

        value = value.Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"1Password returned no value for reference {reference}.");

        // Log that we resolved a reference — never the value, never gated on the capture flag.
        logger.LogInformation("Resolved 1Password reference {Reference} ({Kind}).",
            reference, IsOneTimePassword(field) ? "totp" : "field");
        return value;
    }

    /// <summary>Splits <c>op://Vault/Item/Field</c>. Field may contain spaces ("one-time password").</summary>
    private static (string Vault, string Item, string Field) Parse(string reference)
    {
        const string scheme = "op://";
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith(scheme, StringComparison.Ordinal))
            throw new InvalidOperationException($"Not a 1Password reference: '{reference}'.");

        var parts = reference[scheme.Length..].Split('/', 3);
        if (parts.Length < 3 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Malformed 1Password reference: '{reference}'. Expected op://Vault/Item/field.");

        return (parts[0], parts[1], parts[2]);
    }

    private static bool IsOneTimePassword(string field) =>
        field.Equals("one-time password", StringComparison.OrdinalIgnoreCase)
        || field.Contains("otp", StringComparison.OrdinalIgnoreCase)
        || field.Contains("totp", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 5: Add the resolver to DI**

In `Erda.Core/ServiceCollectionExtensions.cs`, directly under the `IOpCli` registration added in Task 1, add:
```csharp
        services.AddSingleton<Erda.Core.Services.OnePassword.IOpSecretResolver, Erda.Core.Services.OnePassword.OpSecretResolver>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~OpSecretResolverTests"`
Expected: PASS (6 tests).

- [ ] **Step 7: Commit**
```bash
git add Erda.Core/Services/OnePassword/IOpSecretResolver.cs Erda.Core/Services/OnePassword/OpSecretResolver.cs Erda.Core/ServiceCollectionExtensions.cs Erda.Tests/OpSecretResolverTests.cs
git commit -m "feat(browser): OpSecretResolver — op:// refs incl. TOTP, vault-scoped, value-free logs"
```

---

## Task 3: Secret-injection middleware (TDD)

**Files:**
- Create: `Erda.Agents/Tools/SecretInjection.cs`
- Test: `Erda.Tests/SecretInjectionTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Erda.Tests/SecretInjectionTests.cs`:
```csharp
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
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~SecretInjectionTests"`
Expected: FAIL — `SecretInjection` does not exist.

- [ ] **Step 3: Implement the middleware**

Create `Erda.Agents/Tools/SecretInjection.cs`:
```csharp
using Erda.Core.Services.OnePassword;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>
/// Function-invocation middleware for the <b>browser sub-agent only</b>. When the model emits a tool
/// call whose string argument is a 1Password reference (<c>op://…</c>), this resolves it to the real
/// value <i>after</i> the model emitted it, forwards the value to the actual tool (the MCP
/// <c>browser_type</c>), then restores the reference in a <c>finally</c>.
///
/// Ordering matters: this middleware is added <b>after</b> <c>UseOpenTelemetry</c> in
/// <see cref="Erda.Agents.BrowserAgent"/>, so OpenTelemetry is the outer layer and records the
/// argument <i>before</i> the swap (and, if it serializes lazily, <i>after</i> the restore). Either
/// way the recorded/telemetry copy only ever holds the <c>op://…</c> reference — never the resolved
/// secret. This runs regardless of the message-content capture flag.
///
/// Only exact top-level reference strings (a value that starts with <c>op://</c>) are resolved; the
/// browsing prompt instructs the agent to type the bare reference as the field value.
/// </summary>
public static class SecretInjection
{
    public static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>>
        Middleware(IOpSecretResolver resolver) =>
        async (agent, context, next, cancellationToken) =>
        {
            var args = context?.Arguments;
            if (args is null)
                return await next(context!, cancellationToken);

            // Resolve every op:// string arg, remembering the originals to restore afterward.
            List<KeyValuePair<string, object?>>? originals = null;
            foreach (var kv in args.ToList())
            {
                if (kv.Value is string s && s.StartsWith("op://", StringComparison.Ordinal))
                {
                    (originals ??= []).Add(kv);
                    args[kv.Key] = await resolver.ResolveAsync(s, cancellationToken);
                }
            }

            try
            {
                return await next(context!, cancellationToken);
            }
            finally
            {
                if (originals is not null)
                    foreach (var kv in originals) args[kv.Key] = kv.Value;
            }
        };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~SecretInjectionTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**
```bash
git add Erda.Agents/Tools/SecretInjection.cs Erda.Tests/SecretInjectionTests.cs
git commit -m "feat(browser): secret-injection middleware — resolve op:// below the LLM, scrub telemetry"
```

---

## Task 4: Registrable-domain matching (TDD)

**Files:**
- Create: `Erda.Agents/Tools/RegistrableDomain.cs`
- Test: `Erda.Tests/RegistrableDomainTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Erda.Tests/RegistrableDomainTests.cs`:
```csharp
using Erda.Agents.Tools;
using Xunit;

namespace Erda.Tests;

public class RegistrableDomainTests
{
    [Theory]
    [InlineData("moxfield.com", "moxfield.com")]
    [InlineData("www.moxfield.com", "moxfield.com")]
    [InlineData("https://www.moxfield.com/decks/abc", "moxfield.com")]
    [InlineData("https://accounts.google.com/signin", "google.com")]
    [InlineData("HTTPS://WWW.Moxfield.COM", "moxfield.com")]
    [InlineData("foo.bar.co.uk", "bar.co.uk")]          // two-level public suffix
    [InlineData("shop.example.com.au", "example.com.au")]
    public void Of_returns_the_registrable_domain(string input, string expected)
        => Assert.Equal(expected, RegistrableDomain.Of(input));

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("not a url")]
    public void Of_returns_empty_for_unusable_input(string input)
        => Assert.Equal("", RegistrableDomain.Of(input));

    [Fact]
    public void Matches_is_case_insensitive_and_subdomain_tolerant()
    {
        Assert.True(RegistrableDomain.Matches("https://login.moxfield.com", "moxfield.com"));
        Assert.False(RegistrableDomain.Matches("https://example.com", "moxfield.com"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~RegistrableDomainTests"`
Expected: FAIL — `RegistrableDomain` does not exist.

- [ ] **Step 3: Implement `RegistrableDomain`**

Create `Erda.Agents/Tools/RegistrableDomain.cs`:
```csharp
namespace Erda.Agents.Tools;

/// <summary>
/// Pure helper to reduce a host or URL to its <b>registrable domain</b> (eTLD+1), so the
/// <c>find_login</c> lookup matches a 1Password item even when the live page is on a subdomain or an
/// SSO redirect (e.g. <c>login.moxfield.com</c> → <c>moxfield.com</c>).
///
/// This is a pragmatic approximation, not a full Public Suffix List: it takes the last two labels,
/// or the last three when the last two form a known two-level public suffix (<c>co.uk</c> etc.). For
/// a single-user vault with one item per site this is sufficient; a full PSL is a later hardening.
/// </summary>
public static class RegistrableDomain
{
    // Common two-level public suffixes where the registrable domain needs three labels.
    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk",
        "com.au", "net.au", "org.au",
        "co.jp", "co.nz", "co.za", "com.br", "co.in", "co.kr",
    };

    /// <summary>The registrable domain of a host or URL, lowercased; "" if none can be derived.</summary>
    public static string Of(string hostOrUrl)
    {
        var host = ExtractHost(hostOrUrl);
        if (host.Length == 0) return "";

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return "";   // bare "localhost" etc. — not registrable

        var lastTwo = string.Join('.', labels[^2..]);
        if (labels.Length >= 3 && TwoLevelSuffixes.Contains(lastTwo))
            return string.Join('.', labels[^3..]);
        return lastTwo;
    }

    /// <summary>True if <paramref name="hostOrUrl"/> shares a registrable domain with <paramref name="other"/>.</summary>
    public static bool Matches(string hostOrUrl, string other)
    {
        var a = Of(hostOrUrl);
        return a.Length > 0 && a.Equals(Of(other), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractHost(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = input.Trim();

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host.ToLowerInvariant();

        // Not an absolute URL — treat as a bare host. Reject anything with spaces or no dot.
        if (s.Contains(' ')) return "";
        s = s.ToLowerInvariant();
        return s.Contains('.') ? s : "";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~RegistrableDomainTests"`
Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add Erda.Agents/Tools/RegistrableDomain.cs Erda.Tests/RegistrableDomainTests.cs
git commit -m "feat(browser): registrable-domain (eTLD+1) helper for login matching"
```

---

## Task 5: `FindLogin` — parsers, matching, reference building, and the `find_login` tool (TDD)

**Files:**
- Create: `Erda.Agents/Tools/FindLogin.cs`
- Test: `Erda.Tests/FindLoginTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Erda.Tests/FindLoginTests.cs`:
```csharp
using Erda.Agents.Tools;
using Erda.Core.Services.OnePassword;
using Xunit;

namespace Erda.Tests;

public class FindLoginTests
{
    private const string ListJson = """
    [
      { "id": "moxid", "title": "Moxfield", "category": "LOGIN",
        "urls": [ { "label": "website", "primary": true, "href": "https://www.moxfield.com" } ] },
      { "id": "ghid", "title": "GitHub", "category": "LOGIN",
        "urls": [ { "primary": true, "href": "https://github.com" } ] },
      { "id": "noteid", "title": "A secure note", "category": "SECURE_NOTE" }
    ]
    """;

    private const string MoxItemJson = """
    {
      "id": "moxid", "title": "Moxfield",
      "urls": [ { "primary": true, "href": "https://www.moxfield.com" } ],
      "fields": [
        { "id": "username", "type": "STRING", "label": "username", "value": "phil" },
        { "id": "password", "type": "CONCEALED", "label": "password", "value": "x" },
        { "id": "TOTP_abc", "type": "OTP", "label": "one-time password", "totp": "123456" }
      ]
    }
    """;

    [Fact]
    public void ParseList_reads_id_title_and_urls()
    {
        var items = FindLogin.ParseList(ListJson);
        Assert.Equal(3, items.Count);
        var mox = items[0];
        Assert.Equal("moxid", mox.Id);
        Assert.Equal("Moxfield", mox.Title);
        Assert.Equal("https://www.moxfield.com", Assert.Single(mox.Urls));
        Assert.Empty(items[2].Urls);                       // the secure note has no urls
    }

    [Fact]
    public void ParseItem_detects_a_totp_field()
    {
        var detail = FindLogin.ParseItem(MoxItemJson);
        Assert.Equal("moxid", detail.Id);
        Assert.True(detail.HasTotp);
    }

    [Fact]
    public void Match_finds_the_item_by_registrable_domain_via_subdomain()
    {
        var items = FindLogin.ParseList(ListJson);
        var hits = FindLogin.Match("https://login.moxfield.com/account", items);
        Assert.Equal("moxid", Assert.Single(hits).Id);
    }

    [Fact]
    public void Match_returns_empty_when_no_item_matches()
        => Assert.Empty(FindLogin.Match("https://example.org", FindLogin.ParseList(ListJson)));

    [Fact]
    public void BuildReferences_emits_only_references_and_includes_totp_when_present()
    {
        var detail = FindLogin.ParseItem(MoxItemJson);
        var refs = FindLogin.BuildReferences("Erda", detail);

        Assert.Equal("op://Erda/moxid/username", refs.UsernameRef);
        Assert.Equal("op://Erda/moxid/password", refs.PasswordRef);
        Assert.Equal("op://Erda/moxid/one-time password", refs.OneTimePasswordRef);
        // Nothing in the references is a secret value.
        Assert.DoesNotContain("123456", refs.UsernameRef + refs.PasswordRef + refs.OneTimePasswordRef);
    }

    [Fact]
    public void BuildReferences_omits_totp_when_the_item_has_none()
    {
        var noTotp = new OpItemDetail("id1", "Site", ["https://site.com"], HasTotp: false);
        Assert.Null(FindLogin.BuildReferences("Erda", noTotp).OneTimePasswordRef);
    }

    // ---- the tool end-to-end against a fake op CLI ----

    private sealed class FakeOpCli(string list, string item) : IOpCli
    {
        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default) =>
            Task.FromResult(args is ["item", "list", ..] ? list : item);
    }

    [Fact]
    public async Task Tool_returns_references_for_a_single_match_and_never_a_value()
    {
        var tool = FindLogin.CreateTool(new FakeOpCli(ListJson, MoxItemJson), "Erda");
        var result = (string)(await tool.InvokeAsync(new() { ["domain"] = "moxfield.com" }))!;

        Assert.Contains("op://Erda/moxid/username", result);
        Assert.Contains("op://Erda/moxid/password", result);
        Assert.Contains("op://Erda/moxid/one-time password", result);
        Assert.DoesNotContain("123456", result);           // no TOTP value leaks
        Assert.DoesNotContain("\"value\"", result);
    }

    [Fact]
    public async Task Tool_reports_no_login_when_nothing_matches()
    {
        var tool = FindLogin.CreateTool(new FakeOpCli(ListJson, MoxItemJson), "Erda");
        var result = (string)(await tool.InvokeAsync(new() { ["domain"] = "unknown-site.com" }))!;
        Assert.Contains("No login", result);
    }
}
```
> `tool.InvokeAsync(new AIFunctionArguments { ... })` returns `object?`; the `find_login` tool returns a `string`. `AIFunctionArguments` has a collection initializer over `string`→`object?`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~FindLoginTests"`
Expected: FAIL — `FindLogin` does not exist.

- [ ] **Step 3: Implement `FindLogin`**

Create `Erda.Agents/Tools/FindLogin.cs`:
```csharp
using System.ComponentModel;
using System.Text.Json;
using Erda.Core.Services.OnePassword;
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>One item from <c>op item list</c> (id, title, website URLs). No secret fields.</summary>
public sealed record OpItemSummary(string Id, string Title, IReadOnlyList<string> Urls);

/// <summary>One item from <c>op item get</c>: identity, URLs, and whether it carries a TOTP field.</summary>
public sealed record OpItemDetail(string Id, string Title, IReadOnlyList<string> Urls, bool HasTotp);

/// <summary>The <c>op://…</c> references for a login. Never contains secret values.</summary>
public sealed record LoginReferences(string Title, string UsernameRef, string PasswordRef, string? OneTimePasswordRef);

/// <summary>
/// The <c>find_login(domain)</c> tool and its pure helpers. The 1Password <c>Erda</c> vault is the
/// account registry and the allow-list: this lists the vault, matches the page's registrable domain
/// against each item's website, and returns that item's <c>op://…</c> <b>references</b> — never the
/// secret values (those resolve below the LLM via <see cref="SecretInjection"/> at type-time).
///
/// 0 matches → "no login" (fails safe — the vault is the boundary); multiple → an ambiguity result.
/// </summary>
public static class FindLogin
{
    [Description(
        "Look up a saved login for the current site so you can sign in. Pass the site's domain or URL. " +
        "Returns 1Password references (op://…) to type into the username/password (and, if asked, the " +
        "one-time-code) fields — the references resolve to real values securely when you type them. " +
        "If it returns 'No login', you cannot sign in to this site; stop and say so.")]
    private static string Describe() => "";   // marker for the description; real tool is built in CreateTool

    /// <summary>Builds the <c>find_login</c> AIFunction over an <see cref="IOpCli"/> + vault name.</summary>
    public static AIFunction CreateTool(IOpCli cli, string vault)
    {
        async Task<string> FindLoginAsync(
            [Description("The site's domain or full URL, e.g. 'moxfield.com' or the page address.")] string domain,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<OpItemSummary> items;
            try
            {
                var listJson = await cli.RunAsync(["item", "list", "--vault", vault, "--format", "json"], cancellationToken);
                items = ParseList(listJson);
            }
            catch (OpCliException ex)
            {
                return $"Could not reach 1Password to look up a login: {ex.Message}";
            }

            var hits = Match(domain, items);
            if (hits.Count == 0)
                return $"No login found in the {vault} vault for '{domain}'. I cannot sign in to this site.";
            if (hits.Count > 1)
                return $"Multiple logins match '{domain}': {string.Join(", ", hits.Select(h => h.Title))}. " +
                       "Ask which account to use before signing in.";

            OpItemDetail detail;
            try
            {
                var itemJson = await cli.RunAsync(["item", "get", hits[0].Id, "--vault", vault, "--format", "json"], cancellationToken);
                detail = ParseItem(itemJson);
            }
            catch (OpCliException ex)
            {
                return $"Found '{hits[0].Title}' but could not read its details: {ex.Message}";
            }

            var refs = BuildReferences(vault, detail);
            var totp = refs.OneTimePasswordRef is null
                ? "This account has no one-time-code set up."
                : $"If a one-time code / 2FA is requested, type {refs.OneTimePasswordRef}.";

            return $"Found login '{refs.Title}'. Fill the form by typing these 1Password references " +
                   $"verbatim as the field values (they resolve securely): " +
                   $"username = {refs.UsernameRef}; password = {refs.PasswordRef}. {totp} " +
                   "If the site instead shows a captcha or a push/SMS/email challenge, stop and report that you are blocked.";
        }

        return AIFunctionFactory.Create(
            FindLoginAsync,
            new AIFunctionFactoryOptions
            {
                Name = "find_login",
                Description =
                    "Look up a saved login for a site by domain. Returns 1Password references (op://…) to " +
                    "type into the login form — never the secret values. 'No login' means you cannot sign in.",
            });
    }

    /// <summary>Parses <c>op item list --format json</c>: id, title, and any website hrefs.</summary>
    public static IReadOnlyList<OpItemSummary> ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<OpItemSummary>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
            var urls = new List<string>();
            if (el.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
                foreach (var u in urlsEl.EnumerateArray())
                    if (u.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
                        urls.Add(h);
            if (id.Length > 0) result.Add(new OpItemSummary(id, title, urls));
        }
        return result;
    }

    /// <summary>Parses <c>op item get --format json</c>: identity, URLs, and TOTP presence.</summary>
    public static OpItemDetail ParseItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";

        var urls = new List<string>();
        if (root.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
            foreach (var u in urlsEl.EnumerateArray())
                if (u.TryGetProperty("href", out var href) && href.GetString() is { Length: > 0 } h)
                    urls.Add(h);

        var hasTotp = false;
        if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
            foreach (var f in fieldsEl.EnumerateArray())
                if (f.TryGetProperty("type", out var ty) &&
                    string.Equals(ty.GetString(), "OTP", StringComparison.OrdinalIgnoreCase))
                    hasTotp = true;

        return new OpItemDetail(id, title, urls, hasTotp);
    }

    /// <summary>Items whose website shares a registrable domain with <paramref name="domain"/>.</summary>
    public static IReadOnlyList<OpItemSummary> Match(string domain, IReadOnlyList<OpItemSummary> items) =>
        [.. items.Where(i => i.Urls.Any(u => RegistrableDomain.Matches(u, domain)))];

    /// <summary>Builds the op:// references for an item (username, password, and TOTP if present).</summary>
    public static LoginReferences BuildReferences(string vault, OpItemDetail item) => new(
        Title: item.Title,
        UsernameRef: $"op://{vault}/{item.Id}/username",
        PasswordRef: $"op://{vault}/{item.Id}/password",
        OneTimePasswordRef: item.HasTotp ? $"op://{vault}/{item.Id}/one-time password" : null);
}
```
> Remove the unused `Describe()` marker if your linter flags it — it is only documentation; the live description is set inline in `CreateTool`. (Kept out of the final code below; delete it if present.)

Delete the `Describe()` method — it was illustrative. The real description lives in `AIFunctionFactoryOptions.Description`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~FindLoginTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**
```bash
git add Erda.Agents/Tools/FindLogin.cs Erda.Tests/FindLoginTests.cs
git commit -m "feat(browser): find_login(domain) — vault lookup returns op:// refs, never values"
```

---

## Task 6: Wire `find_login` + secret-injection into the browser sub-agent

**Files:**
- Modify: `Erda.Agents/Orchestration/BrowserAgent.cs`

- [ ] **Step 1: Extend the system prompt with the login playbook**

In `Erda.Agents/Orchestration/BrowserAgent.cs`, replace the `SystemPrompt` constant with:
```csharp
    private const string SystemPrompt =
        "You control a real web browser through tools. Work step by step: take a snapshot to see the " +
        "page, then act (navigate/click/type), then snapshot again. Prefer the accessibility snapshot " +
        "over screenshots for deciding actions. When you have the answer, state it concisely.\n\n" +
        "LOGGING IN: if a page requires sign-in, call find_login with the site's domain. It returns " +
        "1Password references (op://…) — type those references verbatim into the username and password " +
        "fields; they resolve to the real credentials securely, so never ask for or guess a password. " +
        "If the site then asks for a one-time code / 2FA, type the one-time-password reference it gave " +
        "you. If find_login says there is no login, or the site shows a captcha or a push/SMS/email " +
        "challenge you cannot complete, STOP and report clearly that you are blocked and why — do not " +
        "guess credentials or codes.";
```

- [ ] **Step 2: Add `find_login` to the tools and `SecretInjection` to the pipeline**

In `TryCreateTool`, resolve the new dependencies near the other `services.GetRequiredService<...>` calls (after the `browser` options line):
```csharp
        var opCli = services.GetRequiredService<Erda.Core.Services.OnePassword.IOpCli>();
        var secretResolver = services.GetRequiredService<Erda.Core.Services.OnePassword.IOpSecretResolver>();
```

Change the tool list to include `find_login`. Replace:
```csharp
        AIAgent agent = chat.AsAIAgent(instructions: SystemPrompt, name: "browser", tools: [.. mcp.Tools])
```
with:
```csharp
        var tools = new List<AITool>(mcp.Tools) { FindLogin.CreateTool(opCli, browser.OnePasswordVault) };

        AIAgent agent = chat.AsAIAgent(instructions: SystemPrompt, name: "browser", tools: tools)
```

Add the middleware **after** `UseOpenTelemetry` (so OTel stays the outer layer and records the reference). Change the builder chain from:
```csharp
            .UseOpenTelemetry(
                sourceName: ObservabilityOptions.ActivitySourceName,
                configure: telemetry => telemetry.EnableSensitiveData = observability.CaptureMessageContent)
            // NOTE: intentionally NOT adding ToolCallActivity.Middleware here — the orchestrator already
            // records the top-level browse_web call; recording every inner navigate/click would flood the
            // LAN activity feed. Granular browser steps live in OTel/Seq instead.
            .Build();
```
to:
```csharp
            .UseOpenTelemetry(
                sourceName: ObservabilityOptions.ActivitySourceName,
                configure: telemetry => telemetry.EnableSensitiveData = observability.CaptureMessageContent)
            // Secret injection runs INSIDE OpenTelemetry (added after it): it swaps op:// references for
            // real values just before the MCP type call and restores the reference in a finally, so the
            // OTel span records only the reference — never the resolved secret. See SecretInjection.
            .Use(SecretInjection.Middleware(secretResolver))
            // NOTE: intentionally NOT adding ToolCallActivity.Middleware here — the orchestrator already
            // records the top-level browse_web call; recording every inner navigate/click would flood the
            // LAN activity feed. Granular browser steps live in OTel/Seq instead.
            .Build();
```

Add `using Erda.Agents.Tools;` at the top of `BrowserAgent.cs` if not already present (it is — `IBrowserMcp` lives there).

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build Erda.slnx && dotnet test Erda.Tests/Erda.Tests.csproj`
Expected: build succeeds; all tests pass (the 200 baseline + the new resolver/middleware/domain/find_login tests).

- [ ] **Step 4: Commit**
```bash
git add Erda.Agents/Orchestration/BrowserAgent.cs
git commit -m "feat(browser): wire find_login + secret-injection into the browser sub-agent"
```

---

## Task 7: Read-only accounts on the Capabilities page (Component 6/9)

**Files:**
- Modify: `Erda.Server/Api/Capabilities/CapabilitiesDtos.cs`
- Modify: `Erda.Server/Api/Capabilities/CapabilitiesEndpoints.cs`
- Modify: `Erda.Tests/CapabilitiesEndpointTests.cs`
- Modify: `web/src/api/types.ts`, `web/src/api/client.ts`, `web/src/views/CapabilitiesView.vue`

- [ ] **Step 1: Add the account DTOs**

Append to `Erda.Server/Api/Capabilities/CapabilitiesDtos.cs`:
```csharp

public sealed record AccountDto(string Title, IReadOnlyList<string> Sites);
public sealed record AccountsResponse(IReadOnlyList<AccountDto> Accounts);
```

- [ ] **Step 2: Write the failing test for the accounts mapping**

In `Erda.Tests/CapabilitiesEndpointTests.cs`, add (the parsing reuses `FindLogin.ParseList`, so the mapping under test is "op item list JSON → titles + sites only, no secrets"):
```csharp
    [Fact]
    public void Maps_op_item_list_to_accounts_without_secrets()
    {
        const string listJson = """
        [ { "id":"moxid", "title":"Moxfield",
            "urls":[ { "primary":true, "href":"https://www.moxfield.com" } ] },
          { "id":"ghid", "title":"GitHub",
            "urls":[ { "href":"https://github.com" } ] } ]
        """;

        var dto = CapabilitiesEndpoints.BuildAccountsResponse(listJson);

        Assert.Equal(2, dto.Accounts.Count);
        Assert.Equal("Moxfield", dto.Accounts[0].Title);
        Assert.Equal("https://www.moxfield.com", Assert.Single(dto.Accounts[0].Sites));
        // The DTO shape physically cannot carry a value field — assert the serialized form has none.
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("value", json, System.StringComparison.OrdinalIgnoreCase);
    }
```
Add `using Erda.Server.Api.Capabilities;` if not already imported in the test file (it is, from Plan 1).

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~CapabilitiesEndpointTests"`
Expected: FAIL — `BuildAccountsResponse` does not exist.

- [ ] **Step 4: Add the endpoint + mapping**

In `Erda.Server/Api/Capabilities/CapabilitiesEndpoints.cs`, add `using Erda.Agents.Tools;` and `using Erda.Core.Configuration;` and `using Erda.Core.Services.OnePassword;` and `using Microsoft.Extensions.Options;` at the top, then add inside `MapCapabilitiesEndpoints` (after the existing `mcp` GET):
```csharp
        group.MapGet("/capabilities/accounts", async (IOpCli op, IOptions<BrowserOptions> browser, CancellationToken ct) =>
        {
            var vault = browser.Value.OnePasswordVault;
            try
            {
                var json = await op.RunAsync(["item", "list", "--vault", vault, "--format", "json"], ct);
                return Results.Ok(BuildAccountsResponse(json));
            }
            catch (OpCliException)
            {
                // 1Password not configured / unreachable — show an empty list, not an error.
                return Results.Ok(new AccountsResponse([]));
            }
        });
```
and add the pure mapping method next to `BuildMcpResponse`:
```csharp
    /// <summary>Maps <c>op item list</c> JSON to the read-only accounts DTO (titles + sites only).</summary>
    public static AccountsResponse BuildAccountsResponse(string opItemListJson)
    {
        var accounts = FindLogin.ParseList(opItemListJson)
            .Select(i => new AccountDto(i.Title, i.Urls))
            .ToList();
        return new AccountsResponse(accounts);
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~CapabilitiesEndpointTests"`
Expected: PASS.

- [ ] **Step 6: SPA — fetch + render the accounts card**

In `web/src/api/types.ts`, after the MCP capabilities block, add:
```ts
export interface AccountDto {
  title: string
  sites: string[]
}
export interface AccountsResponse {
  accounts: AccountDto[]
}
```
In `web/src/api/client.ts`, in the imports from `./types` add `AccountsResponse`, and after `getMcpCapabilities` add:
```ts
export function getAccounts(): Promise<AccountsResponse> {
  return get<AccountsResponse>('/api/capabilities/accounts')
}
```
In `web/src/views/CapabilitiesView.vue` `<script setup>`, extend the imports and add a fetch:
```ts
import { getMcpCapabilities, getAccounts } from '../api/client'
import type { McpServerDto, AccountDto } from '../api/types'

const accounts = ref<AccountDto[]>([])
```
and in the existing `onMounted` body (after the `mcpServers` assignment, inside the `try`), add:
```ts
    accounts.value = (await getAccounts()).accounts
```
Then add a card in `<template>` right after the "Connected MCPs" `Card`:
```html
<Card v-if="accounts.length" flush title="Logins Erda can use" sub="read-only — curated in 1Password">
  <div class="cap-list">
    <div v-for="a in accounts" :key="a.title" class="cap-row">
      <div class="cap-name">
        <span class="ci"><Icon name="globe" /></span>
        <span>{{ a.title }}</span>
      </div>
      <div class="cap-detail">
        <div class="cap-tags">
          <span v-for="s in a.sites" :key="s" class="badge sq b-muted">{{ s }}</span>
        </div>
      </div>
    </div>
  </div>
</Card>
```

- [ ] **Step 7: Type-check + build the SPA**

Run: `cd web && npm run build`
Expected: `vue-tsc` passes and `vite build` succeeds.

- [ ] **Step 8: Commit**
```bash
git add Erda.Server/Api/Capabilities/ Erda.Tests/CapabilitiesEndpointTests.cs web/src/api/types.ts web/src/api/client.ts web/src/views/CapabilitiesView.vue
git commit -m "feat(browser): read-only 'Logins Erda can use' from op item list on the Capabilities page"
```

---

## Task 8: Docker `op` binary + compose/env + README, then manual e2e

**Files:**
- Modify: `Dockerfile`
- Modify: `docker-compose.yml`, `.env.example`
- Modify: `README.md`

- [ ] **Step 1: Add the `op` CLI to the runtime image**

In `Dockerfile`, in the `runtime` stage, after the Playwright MCP block (after its `apt-get clean` line), add:
```dockerfile
# ---- 1Password CLI (op) ----------------------------------------------------
# The op binary resolves op://… secret references and lists the scoped Erda vault for the browser
# sub-agent. Authenticated by OP_SERVICE_ACCOUNT_TOKEN (read-only, one vault) from compose. ARM64
# build for the Jetson; override OP_VERSION/OP_ARCH for another host.
ARG OP_VERSION=2.31.1
ARG OP_ARCH=arm64
RUN curl -fsSL -o /tmp/op.zip \
      "https://cache.agilebits.com/dist/1P/op2/pkg/v${OP_VERSION}/op_linux_${OP_ARCH}_v${OP_VERSION}.zip" \
 && (cd /tmp && unzip -o op.zip op) \
 && mv /tmp/op /usr/local/bin/op \
 && chmod +x /usr/local/bin/op \
 && rm -f /tmp/op.zip \
 && /usr/local/bin/op --version
```
> `unzip` and `curl` are needed; `curl`/`ca-certificates` are already installed by the Playwright block above. Add `unzip` to that block's `apt-get install` list (append `unzip`) so it is present here, OR add a small `apt-get update && apt-get install -y --no-install-recommends unzip` at the start of this RUN. Prefer appending `unzip` to the Playwright block's install line to keep one apt layer.

Append `unzip` to the Playwright block's install line so it reads:
```dockerfile
 && apt-get install -y --no-install-recommends curl ca-certificates gnupg unzip \
```

- [ ] **Step 2: Pass the service-account token through compose**

In `docker-compose.yml`, under the `erda` service `environment:` (next to `Erda__Browser__Enabled`), add:
```yaml
      OP_SERVICE_ACCOUNT_TOKEN: ${OP_SERVICE_ACCOUNT_TOKEN:-}   # read-only, scoped to the Erda 1Password vault
```
In `.env.example`, under the browser section, add:
```
# 1Password service-account token (read-only, scoped to the dedicated "Erda" vault). Required only
# when BROWSER_ENABLED=true and you want unattended logins. Create it in 1Password > Developer >
# Service Accounts, grant read access to ONLY the Erda vault, and paste the token here.
OP_SERVICE_ACCOUNT_TOKEN=
```

- [ ] **Step 3: Document setup + the manual fallback in the README**

In `README.md`, add a short subsection under the browser/capabilities documentation (create the section if the browser feature is not yet documented there):
```markdown
### Browser logins (1Password)

Erda logs into sites using credentials it never sees. Set up a dedicated, least-privilege vault:

1. In the 1Password app, create a vault named **`Erda`**. Add a login item per site Erda may use
   (the item's **website** field drives matching; include the **one-time password** field for TOTP-based
   2FA). Curating this vault is how you control which sites Erda can sign into — Erda has no write access.
2. In 1Password → **Developer → Service Accounts**, create a service account with **read-only** access
   to **only** the `Erda` vault. Copy its token into `OP_SERVICE_ACCOUNT_TOKEN` in `.env`.
3. Set `BROWSER_ENABLED=true`. On the first run for a site, Erda fills the login form from 1Password and
   the session persists on the `browser-data` volume, so later runs skip the login.

**Hard stops:** a captcha or a push/SMS/email challenge cannot be solved unattended — Erda stops and
messages you on WhatsApp. As a fallback you can refresh a session manually: run a headed browser against
the same profile and log in once, then let Erda reuse the persisted session.
```

- [ ] **Step 4: Build everything**

Run: `dotnet build Erda.slnx && dotnet test Erda.Tests/Erda.Tests.csproj`
Expected: green. (The Docker image build with `op` is verified in Step 6; skip locally if Docker isn't available — the binary download layer is large.)

- [ ] **Step 5: Manual e2e — the Moxfield login (the proof the unit tests can't give)**

Prereqs on the dev Mac: `op` installed (`brew install 1password-cli`), a real `OP_SERVICE_ACCOUNT_TOKEN` scoped to a test `Erda` vault holding a Moxfield login, Node + the pinned `@playwright/mcp`, and Chromium (`npx playwright install chromium`).

Run headed so you can watch the login happen:
```bash
Erda__Browser__Enabled=true Erda__Browser__Headless=false \
OP_SERVICE_ACCOUNT_TOKEN=<token> ASPNETCORE_ENVIRONMENT=Development \
dotnet run --project Erda.Server
```
Then, via the panel chat: *"Do I own <some card> on Moxfield? Log in if needed."*

Confirm:
- Erda calls `browse_web`; the sub-agent calls `find_login`, fills the form with `op://…` references, and signs in (watch the headed window).
- The reply reflects the live page.
- In the **Activity feed**, the inner `type` calls are NOT recorded (only the top-level `browse_web`), and no password text appears.
- In **Seq** (Development has content capture on), open the `type` tool span: its arguments show the `op://…` **reference**, **never** the password value. *(This is the design's key security check. If a value leaks here, implement the documented fallback: a thin proxy `AIFunction` wrapping the MCP `browser_type` tool that does the resolve+scrub, instead of the middleware.)*

Verify the `--accounts` panel: `curl -s -H "X-Requested-With: erda-panel" http://localhost:5167/api/capabilities/accounts | jq` shows titles + sites only, no `value`/secret fields.

Verify a hard stop: point a playbook at a site behind a captcha → the sub-agent returns a "blocked" message and the orchestrator can `message_me`.

- [ ] **Step 6: Build the Docker image (op + Playwright layers)**

Run: `docker compose build erda`
Expected: build completes; the `op --version` and Playwright layers succeed.

- [ ] **Step 7: Final commit**
```bash
git add Dockerfile docker-compose.yml .env.example README.md
git commit -m "feat(browser): op CLI in the image + 1Password service-account setup + README"
```

---

## Self-Review

**Spec coverage (Components 4, 5, 6, 7, and 9-accounts):**
- Component 4 (secret-injection middleware) → Task 3, wired in Task 6. ✓ Resolves `op://` after the LLM emits it, forwards the value, scrubs telemetry (restore-in-finally + inner-of-OTel ordering); TOTP resolved at type-time via the resolver. ✓
- Component 5 (1Password integration `IOpSecretResolver`) → Tasks 1–2. ✓ `op read` for plain fields, `op item get --otp` for TOTP (never cached), vault-scoped, value-free logs. ✓ `op` binary in the image → Task 8. ✓
- Component 6 (account registry = the `Erda` vault, no DB table) → Task 5 (`find_login` by registrable domain; 0 → no login, many → ambiguity, refs never values) + Task 7 (read-only accounts panel). ✓ No EF entity/migration anywhere in the plan. ✓
- Component 7 (unattended login + session persistence) → Task 6 system-prompt playbook (find_login → fill with refs → TOTP ref on 2FA → captcha/push/SMS = hard stop). Session persistence is already provided by Plan 1's `--user-data-dir`. Hard stop → orchestrator's existing `message_me`. ✓
- Component 9 (read-only accounts) → Task 7. ✓

**Out of scope (correctly deferred):** screenshots → WhatsApp (Plan 3, Component 8). No `/send-media`, no `SendImageAsync` in this plan. ✓

**Security checks from the spec's Testing section, each has a test or step:**
- Resolver never includes the value in a thrown message → `OpSecretResolverTests` (refuses outside-vault without shelling out; empty value names the ref only). ✓
- Middleware forwards value, records reference → `SecretInjectionTests` (forward + restore + restore-on-throw). ✓
- Vault discovery DTO carries no secret → `CapabilitiesEndpointTests.Maps_op_item_list_to_accounts_without_secrets` asserts no `value` in the serialized DTO. ✓
- `find_login` matches on registrable domain; 0 → no login; many → ambiguity; payload is references only → `FindLoginTests` + `RegistrableDomainTests`. ✓
- Seq span shows the reference, never the password → Task 8 Step 5 manual check, with the documented proxy-AIFunction fallback. ✓

**Placeholder scan:** the only intentionally-variable values are the `op` version/arch ARGs in the Dockerfile (called out) and the registrable-domain approximation (documented as a pragmatic non-PSL choice). The `Describe()` marker in Task 5 is explicitly instructed to be deleted. No "TBD"/"handle errors"/"similar to" steps.

**Type consistency:** `IOpCli.RunAsync(IReadOnlyList<string>)` is used identically by `OpSecretResolver`, `FindLogin.CreateTool`, the accounts endpoint, and every fake. `OpItemSummary`/`OpItemDetail`/`LoginReferences` (Agents) flow from `FindLogin.ParseList`/`ParseItem`/`BuildReferences` into `Match`/`CreateTool` and the accounts DTO mapping. `BrowserOptions.{OpCommand,OnePasswordVault}` (Task 1) are read by `OpCli`, `OpSecretResolver`, `BrowserAgent`, and the accounts endpoint. `SecretInjection.Middleware(IOpSecretResolver)` matches the `ToolCallActivity` delegate shape and the `.Use(...)` site in `BrowserAgent`.

> **One runtime assumption to confirm in Task 8 manual verification** (flagged, not a redesign risk): that `op item list --vault Erda --format json` includes each login item's `urls`. If a given `op` version omits them from the list output, matching finds nothing; the localized fix is to fetch `op item get` per item to read its URLs. `FindLogin.ParseList` already tolerates a missing `urls` array (the item just won't match), so this fails safe.
