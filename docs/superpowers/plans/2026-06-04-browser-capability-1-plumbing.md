# Browser Capability — Plan 1: Browser Plumbing (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Erda an LLM-driven browser via the Playwright MCP server, exposed to the orchestrator as a single `browse_web` tool, and show connected MCP servers + their tools on the Capabilities page. No logins/secrets yet (Plan 2).

**Architecture:** Add the `ModelContextProtocol` C# SDK. A singleton `IBrowserMcp` owns a stdio MCP client that launches `npx @playwright/mcp` as a child process and exposes its tools (`McpClientTool : AIFunction`) plus a status snapshot. A hosted service connects it at startup, before any agent is resolved, so the agent build can read the tools synchronously. `BrowserAgent` builds a second "browser" `AIAgent` over the same Azure client (deployment = `Browser:Deployment`, default = `ChatDeployment`) with those tools and wraps it via `AsAIFunction` as `browse_web`, added to the orchestrator only when `Browser:Enabled`. A new `/api/capabilities/mcp` endpoint surfaces the status for the SPA.

**Tech Stack:** .NET 10, Microsoft Agent Framework (MAF), `ModelContextProtocol` NuGet, Node + `@playwright/mcp` + Chromium (in the Docker image), Vue 3 + Vite SPA.

**Spec:** [`../specs/2026-06-04-erda-browser-capability-design.md`](../specs/2026-06-04-erda-browser-capability-design.md) — this plan covers Components 1, 2, 3, 9. Components 4–8 (secrets, login, screenshots) are Plans 2 and 3.

**Scope boundary:** This plan ends at "Erda can browse a public, no-login page and report what it sees, and the panel lists the browser MCP's tools." Anything touching `op`/1Password/`find_login`/credential injection belongs to Plan 2.

---

## File Structure

**Create:**
- `Erda.Core/Configuration/BrowserOptions.cs` — bound options for the browser feature.
- `Erda.Agents/Tools/IBrowserMcp.cs` — interface: enabled flag, status snapshot, tools, `EnsureStartedAsync`.
- `Erda.Agents/Tools/PlaywrightMcp.cs` — real impl: launches `npx @playwright/mcp` over stdio, caches tools + status.
- `Erda.Agents/Tools/BrowserMcpHostedService.cs` — connects `IBrowserMcp` at startup.
- `Erda.Agents/Orchestration/BrowserAgent.cs` — builds the browser sub-agent and the `browse_web` `AIFunction`; holds the expose-gate decision.
- `Erda.Server/Api/Capabilities/CapabilitiesDtos.cs` — response DTOs.
- `Erda.Server/Api/Capabilities/CapabilitiesEndpoints.cs` — `GET /api/capabilities/mcp`.

**Modify:**
- `Erda.Agents/ServiceCollectionExtensions.cs` (the `AddErdaAgents` site) — register options + `IBrowserMcp` + hosted service.
- `Erda.Agents/Orchestration/ErdaAgent.cs` — add `browse_web` to the tool list when exposed.
- `Erda.Server/Api/PanelApi.cs` — `data.MapCapabilitiesEndpoints();`.
- `web/src/api/types.ts`, `web/src/api/client.ts` — capabilities DTO + fetch.
- `web/src/views/CapabilitiesView.vue` — "Connected MCPs" card + a static "Web browsing" entry.
- `Dockerfile` — Node + `@playwright/mcp` + Chromium in the runtime stage.
- `docker-compose.yml` + `.env.example` — `browser-data` volume + `Erda__Browser__Enabled`.

**Test:**
- `Erda.Tests/BrowserOptionsTests.cs`
- `Erda.Tests/BrowserAgentGateTests.cs`
- `Erda.Tests/CapabilitiesEndpointTests.cs`

> **Testability note:** the real `PlaywrightMcp` launches `npx` and cannot be unit-tested without the binary, so it is **verified manually** (Task 7). Everything that *consumes* it (the expose-gate, the capabilities mapping) is unit-tested against a **fake `IBrowserMcp`**. This is deliberate — we test our logic, not the SDK or Chromium.

---

## Task 1: BrowserOptions + package reference

**Files:**
- Modify: `Erda.Agents/Erda.Agents.csproj`
- Create: `Erda.Core/Configuration/BrowserOptions.cs`
- Test: `Erda.Tests/BrowserOptionsTests.cs`

- [ ] **Step 1: Add the MCP SDK package reference**

Run (from repo root):
```bash
dotnet add Erda.Agents/Erda.Agents.csproj package ModelContextProtocol --prerelease
```
Expected: the `<PackageReference Include="ModelContextProtocol" .../>` line is added and `dotnet restore` succeeds. (`--prerelease` because the SDK ships preview builds, like the MAF packages.)

- [ ] **Step 2: Write the failing test for options binding**

Create `Erda.Tests/BrowserOptionsTests.cs`:
```csharp
using Erda.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class BrowserOptionsTests
{
    [Fact]
    public void Binds_from_Erda_Browser_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Erda:Browser:Enabled"] = "true",
                ["Erda:Browser:Deployment"] = "gpt-5",
                ["Erda:Browser:McpCommand"] = "npx",
                ["Erda:Browser:UserDataDir"] = "/data/browser",
                ["Erda:Browser:MaxSteps"] = "25",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<BrowserOptions>(config.GetSection("Erda:Browser"));
        var opts = services.BuildServiceProvider().GetRequiredService<IOptions<BrowserOptions>>().Value;

        Assert.True(opts.Enabled);
        Assert.Equal("gpt-5", opts.Deployment);
        Assert.Equal("/data/browser", opts.UserDataDir);
        Assert.Equal(25, opts.MaxSteps);
    }

    [Fact]
    public void Defaults_are_disabled_and_safe()
    {
        var opts = new BrowserOptions();
        Assert.False(opts.Enabled);
        Assert.Null(opts.Deployment);          // null => fall back to ChatDeployment
        Assert.Equal("/data/browser", opts.UserDataDir);
        Assert.True(opts.MaxSteps > 0);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "ClassName=Erda.Tests.BrowserOptionsTests"`
Expected: FAIL — `BrowserOptions` does not exist (compile error).

- [ ] **Step 4: Create BrowserOptions**

Create `Erda.Core/Configuration/BrowserOptions.cs`:
```csharp
namespace Erda.Core.Configuration;

/// <summary>
/// Options for the agentic browser feature (Playwright MCP). Bound from the <c>Erda:Browser</c>
/// configuration section. Off by default — when <see cref="Enabled"/> is false the MCP child is
/// never launched and the <c>browse_web</c> tool is not registered.
/// </summary>
public sealed class BrowserOptions
{
    /// <summary>Master switch. When false, no MCP child process and no <c>browse_web</c> tool.</summary>
    public bool Enabled { get; set; }

    /// <summary>Azure AI Foundry deployment for the browser sub-agent. Null => use ErdaOptions.ChatDeployment.</summary>
    public string? Deployment { get; set; }

    /// <summary>Executable that launches the MCP server (stdio).</summary>
    public string McpCommand { get; set; } = "npx";

    /// <summary>Arguments for <see cref="McpCommand"/>. Pinned MCP version + headless + persistent profile.</summary>
    public string[] McpArgs { get; set; } =
        ["@playwright/mcp@0.0.41", "--headless", "--user-data-dir", "/data/browser"];

    /// <summary>Persistent profile directory (kept on the browser-data volume) — the logged-in session.</summary>
    public string UserDataDir { get; set; } = "/data/browser";

    /// <summary>Upper bound on tool calls inside a single browse_web run, to bound a runaway loop.</summary>
    public int MaxSteps { get; set; } = 40;
}
```
> Pin the exact `@playwright/mcp` version (the `0.0.41` above is a placeholder version string — set it to the version you install in the Dockerfile in Task 6; the two must match).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "ClassName=Erda.Tests.BrowserOptionsTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add Erda.Agents/Erda.Agents.csproj Erda.Core/Configuration/BrowserOptions.cs Erda.Tests/BrowserOptionsTests.cs
git commit -m "feat(browser): BrowserOptions + ModelContextProtocol package"
```

---

## Task 2: IBrowserMcp + PlaywrightMcp + hosted service + DI

**Files:**
- Create: `Erda.Agents/Tools/IBrowserMcp.cs`
- Create: `Erda.Agents/Tools/PlaywrightMcp.cs`
- Create: `Erda.Agents/Tools/BrowserMcpHostedService.cs`
- Modify: `Erda.Agents/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Define the interface + status types**

Create `Erda.Agents/Tools/IBrowserMcp.cs`:
```csharp
using Microsoft.Extensions.AI;

namespace Erda.Agents.Tools;

/// <summary>One tool exposed by an MCP server, for the capabilities panel.</summary>
public sealed record McpToolInfo(string Name, string? Description);

/// <summary>A point-in-time snapshot of an MCP server connection, for the capabilities panel.</summary>
public sealed record McpServerStatus(string Name, string Transport, bool Connected, IReadOnlyList<McpToolInfo> Tools);

/// <summary>
/// Owns the lifecycle of the Playwright MCP server (a stdio child process) and exposes its tools to
/// the browser sub-agent. A single instance; connected once at startup by
/// <see cref="BrowserMcpHostedService"/>. When the feature is disabled, this is a no-op:
/// <see cref="Tools"/> is empty and no child process is launched.
/// </summary>
public interface IBrowserMcp
{
    bool Enabled { get; }

    /// <summary>The MCP tools as MAF <see cref="AITool"/>s (empty until connected, or if disabled/failed).</summary>
    IReadOnlyList<AITool> Tools { get; }

    /// <summary>Snapshot for the capabilities panel.</summary>
    McpServerStatus Status { get; }

    /// <summary>Idempotent connect. Safe to call when disabled (no-op). Never throws — failures leave Tools empty and Connected=false.</summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Implement PlaywrightMcp**

Create `Erda.Agents/Tools/PlaywrightMcp.cs`:
```csharp
using Erda.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Erda.Agents.Tools;

/// <summary>
/// Real <see cref="IBrowserMcp"/>: launches <c>npx @playwright/mcp</c> over stdio and lists its tools.
/// Connect is best-effort and idempotent; any failure is logged and leaves the server "not connected"
/// (empty tools), so the rest of the app starts normally and the panel shows it as down.
/// </summary>
public sealed class PlaywrightMcp(IOptions<BrowserOptions> options, ILogger<PlaywrightMcp> logger) : IBrowserMcp, IAsyncDisposable
{
    private const string ServerName = "playwright";

    private readonly BrowserOptions _opts = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<AITool> _tools = [];
    private bool _connected;

    public bool Enabled => _opts.Enabled;
    public IReadOnlyList<AITool> Tools => _tools;

    public McpServerStatus Status => new(
        Name: ServerName,
        Transport: "stdio",
        Connected: _connected,
        Tools: _tools.Select(t => new McpToolInfo(t.Name, (t as AIFunction)?.Description)).ToList());

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (!_opts.Enabled || _connected) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connected) return;

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = ServerName,
                Command = _opts.McpCommand,
                Arguments = _opts.McpArgs,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            });

            _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            IList<McpClientTool> tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
            _tools = [.. tools]; // McpClientTool : AIFunction : AITool
            _connected = true;
            logger.LogInformation("Playwright MCP connected: {Count} tools.", _tools.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Playwright MCP failed to connect; browse_web will be unavailable.");
            _tools = [];
            _connected = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
```
> If `McpClient.CreateAsync` is named differently in the installed SDK version, the symbol error will surface at build; the type lives in `ModelContextProtocol.Client`. Verified against the SDK docs: `McpClient.CreateAsync(StdioClientTransport)` + `ListToolsAsync()` returning `IList<McpClientTool>`.

- [ ] **Step 3: Implement the hosted service**

Create `Erda.Agents/Tools/BrowserMcpHostedService.cs`:
```csharp
using Microsoft.Extensions.Hosting;

namespace Erda.Agents.Tools;

/// <summary>
/// Connects the browser MCP at startup, before any agent is resolved, so the synchronous agent build
/// can read <see cref="IBrowserMcp.Tools"/>. No-op when the feature is disabled.
/// </summary>
public sealed class BrowserMcpHostedService(IBrowserMcp mcp) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => mcp.EnsureStartedAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: Register in DI**

Find the registration site:
```bash
grep -n "AddErdaAgents\|ObsidianTools" Erda.Agents/ServiceCollectionExtensions.cs
```
In the `AddErdaAgents` method, alongside the existing tool registrations, add:
```csharp
services.Configure<BrowserOptions>(configuration.GetSection("Erda:Browser"));
services.AddSingleton<IBrowserMcp, PlaywrightMcp>();
services.AddHostedService<BrowserMcpHostedService>();
```
Add `using Erda.Agents.Tools;` and `using Erda.Core.Configuration;` if not present. (`configuration` is the `IConfiguration` already used in that method to bind `ErdaOptions`; if the method signature lacks it, mirror how `ErdaOptions` is bound there.)

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build Erda.slnx`
Expected: build succeeds (no test yet — this task is wiring; behavior is verified in Task 7).

- [ ] **Step 6: Commit**

```bash
git add Erda.Agents/Tools/IBrowserMcp.cs Erda.Agents/Tools/PlaywrightMcp.cs Erda.Agents/Tools/BrowserMcpHostedService.cs Erda.Agents/ServiceCollectionExtensions.cs
git commit -m "feat(browser): IBrowserMcp + Playwright stdio client + startup connect"
```

---

## Task 3: Browser sub-agent + `browse_web` tool

**Files:**
- Create: `Erda.Agents/Orchestration/BrowserAgent.cs`
- Modify: `Erda.Agents/Orchestration/ErdaAgent.cs:45-50` (the tool-list assembly)
- Test: `Erda.Tests/BrowserAgentGateTests.cs`

- [ ] **Step 1: Write the failing test for the expose-gate**

Create `Erda.Tests/BrowserAgentGateTests.cs`:
```csharp
using Erda.Agents.Orchestration;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class BrowserAgentGateTests
{
    private sealed class FakeMcp(bool enabled, int toolCount) : IBrowserMcp
    {
        public bool Enabled => enabled;
        public IReadOnlyList<AITool> Tools { get; } =
            [.. Enumerable.Range(0, toolCount).Select(i => AIFunctionFactory.Create(() => "x", $"browser_tool_{i}"))];
        public McpServerStatus Status => new("playwright", "stdio", enabled, []);
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void ShouldExpose_true_when_enabled_and_tools_present()
        => Assert.True(BrowserAgent.ShouldExpose(new FakeMcp(enabled: true, toolCount: 3)));

    [Fact]
    public void ShouldExpose_false_when_disabled()
        => Assert.False(BrowserAgent.ShouldExpose(new FakeMcp(enabled: false, toolCount: 3)));

    [Fact]
    public void ShouldExpose_false_when_enabled_but_no_tools_connected()
        => Assert.False(BrowserAgent.ShouldExpose(new FakeMcp(enabled: true, toolCount: 0)));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "ClassName=Erda.Tests.BrowserAgentGateTests"`
Expected: FAIL — `BrowserAgent` does not exist.

- [ ] **Step 3: Implement BrowserAgent**

Create `Erda.Agents/Orchestration/BrowserAgent.cs`:
```csharp
using System.ClientModel;
using Azure.AI.OpenAI;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Erda.Agents.Orchestration;

/// <summary>
/// Builds the browser sub-agent and exposes it to the orchestrator as the single <c>browse_web</c>
/// tool (agent-as-tool, like <see cref="Erda.Agents.Workflows.VoiceMemoWorkflow.CreateTool"/>). The
/// sub-agent runs its own multi-step loop with the Playwright MCP tools, so the page snapshots stay
/// out of the orchestrator's context. Its model is <c>Browser:Deployment</c>, defaulting to the
/// orchestrator's <c>ChatDeployment</c>.
/// </summary>
public static class BrowserAgent
{
    public const string ToolName = "browse_web";

    public const string ToolDescription =
        "Perform a web task in a real browser (navigate, read, interact) and return the result. " +
        "Provide the task in plain language, e.g. 'open example.com and tell me the main heading'.";

    private const string SystemPrompt =
        "You control a real web browser through tools. Work step by step: take a snapshot to see the " +
        "page, then act (navigate/click/type), then snapshot again. Prefer the accessibility snapshot " +
        "over screenshots for deciding actions. When you have the answer, state it concisely. If a page " +
        "blocks you (captcha, login you cannot complete), stop and say so rather than guessing.";

    /// <summary>True when the feature is on and the MCP actually connected with at least one tool.</summary>
    public static bool ShouldExpose(IBrowserMcp mcp) => mcp.Enabled && mcp.Tools.Count > 0;

    /// <summary>
    /// Build the <c>browse_web</c> function, or null when <see cref="ShouldExpose"/> is false.
    /// Requires Azure credentials (same as the orchestrator); returns null if unconfigured.
    /// </summary>
    public static AIFunction? TryCreateTool(IServiceProvider services)
    {
        var mcp = services.GetRequiredService<IBrowserMcp>();
        if (!ShouldExpose(mcp)) return null;

        var configuration = services.GetRequiredService<IConfiguration>();
        var erda = services.GetRequiredService<IOptions<ErdaOptions>>().Value;
        var browser = services.GetRequiredService<IOptions<BrowserOptions>>().Value;

        var endpoint = configuration["AZURE_OPENAI_ENDPOINT"];
        var apiKey = configuration["AZURE_OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey)) return null;

        var deployment = string.IsNullOrWhiteSpace(browser.Deployment) ? erda.ChatDeployment : browser.Deployment!;

        ChatClient chat = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey))
            .GetChatClient(deployment);

        AIAgent agent = chat.AsAIAgent(
            instructions: SystemPrompt,
            name: "browser",
            tools: [.. mcp.Tools]);

        return agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = ToolName,
            Description = ToolDescription,
        });
    }
}
```
> `agent.AsAIFunction(AIFunctionFactoryOptions)` is the same agent-as-tool call `VoiceMemoWorkflow.CreateTool` uses (there it's on a workflow-agent; here on a chat agent). If the overload differs in the installed MAF version, mirror exactly what `VoiceMemoWorkflow.cs:70` does.

- [ ] **Step 4: Run the gate test to verify it passes**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "ClassName=Erda.Tests.BrowserAgentGateTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Wire browse_web into the orchestrator**

In `Erda.Agents/Orchestration/ErdaAgent.cs`, immediately after the existing tool registrations (after `tools.AddRange(...ReminderTools...)` at line ~50), add:
```csharp
        var browseTool = BrowserAgent.TryCreateTool(services);
        if (browseTool is not null) tools.Add(browseTool);
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build Erda.slnx`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add Erda.Agents/Orchestration/BrowserAgent.cs Erda.Agents/Orchestration/ErdaAgent.cs Erda.Tests/BrowserAgentGateTests.cs
git commit -m "feat(browser): browser sub-agent exposed as browse_web tool"
```

---

## Task 4: Capabilities API (`GET /api/capabilities/mcp`)

**Files:**
- Create: `Erda.Server/Api/Capabilities/CapabilitiesDtos.cs`
- Create: `Erda.Server/Api/Capabilities/CapabilitiesEndpoints.cs`
- Modify: `Erda.Server/Api/PanelApi.cs:69` (add the map call)
- Test: `Erda.Tests/CapabilitiesEndpointTests.cs`

- [ ] **Step 1: Write the failing test for the status→DTO mapping**

Create `Erda.Tests/CapabilitiesEndpointTests.cs`:
```csharp
using Erda.Agents.Tools;
using Erda.Server.Api.Capabilities;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

public class CapabilitiesEndpointTests
{
    private sealed class FakeMcp : IBrowserMcp
    {
        public bool Enabled => true;
        public IReadOnlyList<AITool> Tools => [];
        public McpServerStatus Status => new("playwright", "stdio", true,
            [new McpToolInfo("browser_navigate", "Go to a URL"), new McpToolInfo("browser_click", null)]);
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void Maps_mcp_status_to_dto()
    {
        var dto = CapabilitiesEndpoints.BuildMcpResponse(new FakeMcp());

        var server = Assert.Single(dto.Servers);
        Assert.Equal("playwright", server.Name);
        Assert.Equal("stdio", server.Transport);
        Assert.True(server.Connected);
        Assert.Equal(2, server.Tools.Count);
        Assert.Equal("browser_navigate", server.Tools[0].Name);
        Assert.Equal("Go to a URL", server.Tools[0].Description);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "ClassName=Erda.Tests.CapabilitiesEndpointTests"`
Expected: FAIL — `CapabilitiesEndpoints` / DTOs do not exist.

- [ ] **Step 3: Create the DTOs**

Create `Erda.Server/Api/Capabilities/CapabilitiesDtos.cs`:
```csharp
namespace Erda.Server.Api.Capabilities;

public sealed record McpToolDto(string Name, string? Description);
public sealed record McpServerDto(string Name, string Transport, bool Connected, IReadOnlyList<McpToolDto> Tools);
public sealed record McpCapabilitiesResponse(IReadOnlyList<McpServerDto> Servers);
```

- [ ] **Step 4: Create the endpoint group**

Create `Erda.Server/Api/Capabilities/CapabilitiesEndpoints.cs`:
```csharp
using Erda.Agents.Tools;

namespace Erda.Server.Api.Capabilities;

/// <summary>The <c>/api/capabilities/mcp</c> endpoint backing the Capabilities page's "Connected MCPs" panel.</summary>
public static class CapabilitiesEndpoints
{
    public static RouteGroupBuilder MapCapabilitiesEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/capabilities/mcp", (IBrowserMcp mcp) => Results.Ok(BuildMcpResponse(mcp)));
        return group;
    }

    /// <summary>Pure mapping from the MCP status snapshot to the API DTO (unit-tested).</summary>
    public static McpCapabilitiesResponse BuildMcpResponse(IBrowserMcp mcp)
    {
        var s = mcp.Status;
        var servers = new List<McpServerDto>
        {
            new(s.Name, s.Transport, s.Connected, s.Tools.Select(t => new McpToolDto(t.Name, t.Description)).ToList()),
        };
        return new McpCapabilitiesResponse(servers);
    }
}
```

- [ ] **Step 5: Map it in PanelApi**

In `Erda.Server/Api/PanelApi.cs`, in `MapPanelApi`, after `data.MapStatusEndpoints();` add:
```csharp
        data.MapCapabilitiesEndpoints();
```
Add `using Erda.Server.Api.Capabilities;` at the top.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "ClassName=Erda.Tests.CapabilitiesEndpointTests"`
Expected: PASS (1 test).

- [ ] **Step 7: Commit**

```bash
git add Erda.Server/Api/Capabilities/ Erda.Server/Api/PanelApi.cs Erda.Tests/CapabilitiesEndpointTests.cs
git commit -m "feat(browser): /api/capabilities/mcp endpoint"
```

---

## Task 5: SPA — "Connected MCPs" panel

**Files:**
- Modify: `web/src/api/types.ts`
- Modify: `web/src/api/client.ts`
- Modify: `web/src/views/CapabilitiesView.vue`

- [ ] **Step 1: Add the DTO types**

Inspect the existing shape first:
```bash
grep -n "export interface\|export type" web/src/api/types.ts | head
```
Append to `web/src/api/types.ts`:
```ts
export interface McpToolDto {
  name: string
  description: string | null
}
export interface McpServerDto {
  name: string
  transport: string
  connected: boolean
  tools: McpToolDto[]
}
export interface McpCapabilitiesResponse {
  servers: McpServerDto[]
}
```

- [ ] **Step 2: Add the fetch to the client**

Inspect the existing client pattern first:
```bash
grep -n "export\|function\|fetch\|get(" web/src/api/client.ts | head -30
```
Add a method mirroring an existing GET (e.g. how `status` or `activity` is fetched). Example shape — adapt names to the file's existing helper:
```ts
export function getMcpCapabilities(): Promise<McpCapabilitiesResponse> {
  return api.get<McpCapabilitiesResponse>('/capabilities/mcp')
}
```
(Use whatever `api.get`/`http` helper the file already defines, and import `McpCapabilitiesResponse` from `./types`.)

- [ ] **Step 3: Render the panel in CapabilitiesView**

In `web/src/views/CapabilitiesView.vue`:
1. In `<script setup>`, fetch on mount:
```ts
import { ref, onMounted } from 'vue'
import { getMcpCapabilities } from '../api/client'
import type { McpServerDto } from '../api/types'
import StatusBadge from '../components/StatusBadge.vue'

const mcpServers = ref<McpServerDto[]>([])
onMounted(async () => {
  try { mcpServers.value = (await getMcpCapabilities()).servers } catch { /* panel just stays empty */ }
})
```
2. Add a static "Web browsing" entry to the existing `onRequest` array:
```ts
{
  icon: 'globe',
  title: 'Web browsing',
  desc: 'Drives a real browser to read sites and complete tasks, like a person would.',
  tags: ['Playwright MCP', 'agentic'],
},
```
3. Add a new `Card` after the existing ones in `<template>` (mirror the existing `cap-row` markup):
```html
<Card v-if="mcpServers.length" flush title="Connected MCPs" sub="tool servers Erda is wired to">
  <div class="cap-list">
    <div v-for="s in mcpServers" :key="s.name" class="cap-row">
      <div class="cap-name">
        <span class="ci"><Icon name="globe" /></span>
        <span>{{ s.name }}</span>
        <StatusBadge :ok="s.connected" />
      </div>
      <div class="cap-detail">
        <div class="cap-tags">
          <span v-for="t in s.tools" :key="t.name" class="badge sq b-muted">{{ t.name }}</span>
        </div>
      </div>
    </div>
  </div>
</Card>
```
> Check `StatusBadge.vue`'s actual prop name with `grep -n "defineProps\|props" web/src/components/StatusBadge.vue` and adjust `:ok` to match (e.g. `:online`/`:status`).

- [ ] **Step 4: Type-check + build the SPA**

Run: `cd web && npm run build`
Expected: `vue-tsc` passes and `vite build` succeeds with no type errors.

- [ ] **Step 5: Commit**

```bash
git add web/src/api/types.ts web/src/api/client.ts web/src/views/CapabilitiesView.vue
git commit -m "feat(browser): Connected MCPs panel on the Capabilities page"
```

---

## Task 6: Docker — Node + Playwright MCP + Chromium

**Files:**
- Modify: `Dockerfile` (runtime stage)
- Modify: `docker-compose.yml`, `.env.example`

- [ ] **Step 1: Add Node + the MCP server + Chromium to the runtime stage**

In `Dockerfile`, in the `runtime` stage (after the `COPY --from=codex ...` line), add:
```dockerfile
# ---- browser (Playwright MCP) ----------------------------------------------
# Node + the pinned Playwright MCP server + a Chromium build, installed to a world-readable path so
# the container (running as uid 1000) can launch it. Pin must match BrowserOptions.McpArgs.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ARG PLAYWRIGHT_MCP_VERSION=0.0.41
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g "@playwright/mcp@${PLAYWRIGHT_MCP_VERSION}" \
 && npx --yes playwright install --with-deps chromium \
 && chmod -R a+rx /ms-playwright \
 && apt-get clean && rm -rf /var/lib/apt/lists/*
```
> Set `PLAYWRIGHT_MCP_VERSION` to the same version as `BrowserOptions.McpArgs` (Task 1). Because the package is installed globally, the MCP command can also be `@playwright/mcp` via `npx`, which resolves the global install.

- [ ] **Step 2: Add the profile volume + enable flag to compose**

In `docker-compose.yml`, under the `erda` service `environment:` add:
```yaml
      Erda__Browser__Enabled: ${BROWSER_ENABLED:-false}
```
Under the `erda` service `volumes:` add:
```yaml
      - browser-data:/data/browser    # persistent browser profile (logged-in session)
```
Under the top-level `volumes:` add:
```yaml
  browser-data:
```
In `.env.example` add:
```
# Agentic browser (Playwright MCP). Off by default; set true to enable browse_web.
BROWSER_ENABLED=false
```
> The `browser-data` named volume is created root-owned by Docker; chown it to 1000:1000 once on the host, same as the `erda-data`/`media` volumes (note this in the README deploy step in Plan 2's docs task, or add it now).

- [ ] **Step 3: Build the image to verify**

Run: `docker compose build erda`
Expected: build completes; the `npm install` + `playwright install` layers succeed. (Large layer — first build is slow.)

- [ ] **Step 4: Commit**

```bash
git add Dockerfile docker-compose.yml .env.example
git commit -m "feat(browser): Node + Playwright MCP + Chromium in the runtime image"
```

---

## Task 7: End-to-end verification (manual)

**No code — this proves the integration the unit tests can't.**

- [ ] **Step 1: Run locally with the browser enabled**

Ensure Node + the MCP are available locally (`npm install -g @playwright/mcp@<pinned> && npx playwright install chromium`), then run the backend with the feature on:
```bash
Erda__Browser__Enabled=true ASPNETCORE_ENVIRONMENT=Development dotnet run --project Erda.Server
```
Expected log line: `Playwright MCP connected: N tools.` (N is ~20+).

- [ ] **Step 2: Verify the capabilities endpoint**

Run:
```bash
curl -s -H "X-Requested-With: erda-panel" http://localhost:5167/api/capabilities/mcp | jq
```
Expected: JSON with `servers[0].name == "playwright"`, `connected == true`, and a non-empty `tools` array (e.g. `browser_navigate`, `browser_click`, `browser_snapshot`).

- [ ] **Step 3: Drive a no-login browse task**

Via the panel chat (or a WhatsApp message if configured), send:
> "Use the browser to open example.com and tell me the page's main heading."

Expected: Erda calls `browse_web`, the sub-agent navigates + snapshots, and Erda replies with "Example Domain" (or the page's H1). Confirm in the Activity feed that a `tool_call` for `browse_web` is recorded.

- [ ] **Step 4: Verify the SPA panel**

Open `http://localhost:5167/capabilities` (prod build) or the Vite dev server. Confirm a "Connected MCPs" card shows `playwright` with a connected badge and tool chips, plus the static "Web browsing" entry.

- [ ] **Step 5: Verify it's off by default**

Restart without the env var (`dotnet run --project Erda.Server`). Confirm: no MCP connect log, `browse_web` absent (ask Erda to browse → it says it can't), and the capabilities endpoint shows `connected: false`.

- [ ] **Step 6: Final commit (if any tweaks were needed)**

```bash
git add -A && git commit -m "chore(browser): plan-1 verification fixups"
```

---

## Self-Review

**Spec coverage (Components 1, 2, 3, 9):**
- Component 1 (Playwright MCP in image) → Task 6. ✓
- Component 2 (MCP client wiring) → Task 2. ✓
- Component 3 (browser sub-agent + browse_web) → Task 3. ✓
- Component 9 (Capabilities "Connected MCPs") → Tasks 4–5. ✓
- The async-startup spike → resolved via `BrowserMcpHostedService` (Task 2) so the synchronous agent build reads ready tools. ✓

**Out of scope (correctly deferred to Plan 2/3):** `op`/1Password, `find_login`, secret-injection middleware, the actual Moxfield login, screenshots→WhatsApp. None appear in this plan.

**Placeholder scan:** the only intentionally-variable value is the pinned `@playwright/mcp` version string, called out in Tasks 1 and 6 with an explicit "these must match" instruction. No "TBD"/"handle errors"/"similar to" steps.

**Type consistency:** `IBrowserMcp` (`Enabled`, `Tools`, `Status`, `EnsureStartedAsync`) is used identically in Tasks 2, 3, 4, and the test fakes. `McpServerStatus`/`McpToolInfo` (Agents) map to `McpServerDto`/`McpToolDto`/`McpCapabilitiesResponse` (Server) in Task 4. `BrowserAgent.ShouldExpose`/`TryCreateTool`/`ToolName` are consistent across Task 3 and its test. `BrowserOptions` fields match between Task 1 and Tasks 2–3.

> **Two symbol risks to watch at build time** (both verified against current docs, flagged only because the MAF/MCP packages are preview): `McpClient.CreateAsync` (namespace `ModelContextProtocol.Client`) and `AIAgent.AsAIFunction(AIFunctionFactoryOptions)` (mirror `VoiceMemoWorkflow.cs:70`). If either name differs in the installed package, the compile error points straight at it; the fix is a rename, not a redesign.
