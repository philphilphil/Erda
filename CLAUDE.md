# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
make dev          # backend + SPA only, no bridge (Ctrl-C kills both); needs node/npx
make dev-all      # run everything: backend + SPA + WhatsApp bridge (one Ctrl-C kills all); needs node/npx + go
make web          # control-panel SPA dev server only (Vite at :5173, proxies /api -> :5167)
make deploy       # git pull && docker compose up -d --build (server only)

dotnet build Erda.slnx                     # build the whole solution
dotnet test Erda.Tests/Erda.Tests.csproj   # run all tests (point at the test project, not the repo root)
dotnet test Erda.Tests/Erda.Tests.csproj --filter "ClassName=ErrorSignatureTests"  # one test class

cd web && npm ci && npm run build   # build the SPA (vue-tsc type-check + vite build)
```

Tests live in `Erda.Tests/` (xUnit) and reference all three code projects. Always pass the test
project explicitly: `dotnet test Erda.Tests/Erda.Tests.csproj` (or `dotnet test Erda.slnx`) — a bare
`dotnet test`/`dotnet run` at the repo root is ambiguous across the solution's projects.

EF migrations: `dotnet ef migrations add <Name> --project Erda.Core --startup-project Erda.Server`
(the `ErdaDbContext` + migrations live in `Erda.Core/Data`).

## Architecture

Erda is a **.NET 10 solution** built on the **Microsoft Agent Framework (MAF) 1.8.0**. It runs as a single orchestrator agent (`erda`) that routes to tools and a workflow.

### Solution layout (`Erda.slnx`)

Four projects with one-directional references — **`Erda.Server` → `Erda.Agents` → `Erda.Core`**.
Shared TFM/`Nullable`/`ImplicitUsings` live in `Directory.Build.props`.

- **`Erda.Core`** (`Microsoft.NET.Sdk`) — host-agnostic business logic: `Configuration/`, `Data/` (EF + migrations), `Services/` (Vault, Codex, Transcriber, ActivityRecorder, clock, `Seq/`), `Scheduling/` (`Reminders/` + `ErrorWatch/`), `WhatsApp/` (channel/sender/queue/worker), and `Abstractions/` (the `IAgentResponder`/`IMemoProcessor` seams that keep Core free of any MAF/ASP.NET dependency). `AddErdaCore()` wires it all.
- **`Erda.Agents`** (`Microsoft.NET.Sdk`) — the MAF layer: `Orchestration/` (the `erda` agent + responder), `Tools/`, `Workflows/`. `AddErdaAgents()` wires it.
- **`Erda.Server`** (`Microsoft.NET.Sdk.Web`) — the only runnable app: `Program.cs`, `Api/`, `WhatsApp/WhatsAppEndpoints.cs`, `Hosting/`. Serves the SPA from `wwwroot`.
- **`Erda.Tests`** (xUnit) — references all three.

### The three-credential model

| Capability | Client | Key |
|---|---|---|
| Chat agent (`gpt-5.4-mini`) | OpenAI SDK → Azure `/openai/v1` | `AZURE_OPENAI_ENDPOINT` (the `…/openai/v1` URL) + `AZURE_OPENAI_API_KEY` |
| Transcription (`gpt-4o-transcribe`) | OpenAI SDK | `OPENAI_API_KEY` |
| Codex (`gpt-5.5`) | `codex` CLI subprocess | ChatGPT subscription session in `~/.codex` |

**Critical:** `CodexRunner.cs` strips `OPENAI_API_KEY` from the Codex subprocess environment so Codex authenticates via ChatGPT subscription, not per-token billing. Never remove this stripping.

### Key source files

- `Erda.Server/Program.cs` — host wiring: Serilog, OpenTelemetry, then `AddErdaCore()` + `AddErdaAgents()` + `AddPanelApi()`, the agent registration, and the request pipeline (SPA static hosting + `/api` + WhatsApp endpoint)
- `Erda.Server/Api/` — control-panel JSON API, grouped by feature (`Reminders/`, `Prompts/`, `Activity/` incl. the `/api/activity/stream` SSE feed, `Config/`, `Auth/`); `PanelApi` wires the groups; `CsrfEndpointFilter` (requires `X-Requested-With: erda-panel` on mutations); per-feature DTOs; `ReminderView`/`ConfigPanelService`/`PanelCredentials` hold panel logic
- `web/` — Vue 3 + Vite + TS control-panel SPA (4 routes + login). Dev: `make web` (Vite proxies `/api` to the backend). Prod: `npm run build` → `web/dist`, copied into `wwwroot` by the Dockerfile and served with `MapFallbackToFile("index.html")`
- `Erda.Agents/Orchestration/ErdaAgent.cs` — orchestrator: system prompt + tool registration (vault tools, `consult_codex`, `process_voice_memo`)
- `Erda.Agents/Orchestration/ErdaAgentResponder.cs` — implements `Erda.Core.Abstractions.IAgentResponder`; adapts agent turns for the WhatsApp channel
- `Erda.Agents/Tools/` — `ObsidianTools` (5 vault tools, confined to `VaultPath`), `ReasoningTools` (`consult_codex`), `ReminderTools`, `NotifyTools`
- `Erda.Core/Services/CodexRunner.cs` — `codex exec` subprocess wrapper; strips `OPENAI_API_KEY`; optional web search
- `Erda.Core/Services/VaultService.cs` — path-safe file I/O under `VaultPath`
- `Erda.Core/Services/Transcriber.cs` — OpenAI audio transcription
- `Erda.Agents/Workflows/VoiceMemoWorkflow.cs` — voice memo pipeline (transcribe → Codex → write); wrapped as `process_voice_memo` tool via `AsAIFunction`; implements `IMemoProcessor` via `MemoProcessor`
- `Erda.Core/Scheduling/ErrorWatch/ErrorWatchScheduler.cs` — background loop: polls Seq for errors, deduplicates by signature, analyzes with Codex, alerts via WhatsApp
- `Erda.Core/WhatsApp/` — bridge integration: inbound queue, background worker, channel service (dispatches text/voice/image to the agent), sender; the HTTP endpoint is `Erda.Server/WhatsApp/WhatsAppEndpoints.cs`

### MAF-specific patterns

- Chat agent: `new ChatClient(model: deployment, credential: new ApiKeyCredential(key), options: new OpenAIClientOptions { Endpoint = new Uri(azureV1Url) }).AsAIAgent(...)`. The stock **OpenAI SDK** (`OpenAI`/`OpenAI.Chat`) pointed at Azure's unified `/openai/v1` surface — **not** `Azure.AI.OpenAI` (dropped: its dated `api-version` made newer Azure models fail, and the OpenAI client is provider-portable). Uses `System.ClientModel.ApiKeyCredential`. We deliberately use **Chat Completions, not the Responses API** — Chat Completions is the universal OpenAI-compatible surface, so swapping providers is just endpoint+key+deployment.
- Workflow-as-tool: the voice-memo workflow is `workflow.AsAIAgent(...).AsAIFunction(...)`. Its start executor must accept `List<ChatMessage>` + `TurnToken` (a plain `string` start executor fails with "Workflow does not support ChatProtocol").
- **Agent `name` matches its registration key** (both `"erda"`): registered with `builder.AddAIAgent(ErdaAgent.Name, …)` and resolved by keyed DI via `[FromKeyedServices("erda")]` in `ErdaAgentResponder` and `WebChatService`.

### Observability

OpenTelemetry traces exported over OTLP to Seq (`{Seq:ServerUrl}/ingest/otlp/v1/traces`). Every span is tagged `app = Erda` — filter `app = 'Erda'` in Seq (not `service.name`, which lands under `@ra` and is not filterable). Content capture (prompts, tool args) is off in production, on in Development.

### WhatsApp channel

The `whatsapp-bridge` (Go) handles the WhatsApp socket and posts inbound messages to `POST /whatsapp/inbound`. `WhatsAppInboundWorker` drains the queue and calls `WhatsAppChannelService`, which enforces the owner whitelist and dispatches by message type. The bridge and Erda share a `/media` Docker volume for downloaded audio/images.

### Control panel (Vue SPA + JSON API)

A single-user, LAN-only web UI replaces the former Blazor Server panel. The backend exposes a JSON API under `/api/*` (minimal-API groups in `Api/`) over the same DB-backed services; the frontend is a Vue 3 SPA in `web/`. **Reminders are live** (the scheduler reads the DB each tick) and **prompt edits apply on restart** (`POST /api/config/restart` → `IHostApplicationLifetime.StopApplication()`; Docker `restart: unless-stopped` brings it back). **Config is env-only and the Config page is read-only** — it surfaces the effective loaded values (secrets masked); to change a setting, edit `.env` and restart. Live activity is pushed over **SSE** (`GET /api/activity/stream`), bridging `IActivityRecorder.Recorded`. Auth is **cookie-based, off by default** — open on the LAN unless `Panel__Password` is set; CSRF is guarded by `SameSite=Lax` + a required `X-Requested-With: erda-panel` header on mutations (no `Secure` flag, since the panel is plain-HTTP on the LAN). Dev: Vite (`:5173`) proxies `/api` to the backend (`:5167`); prod: the Vite build is served from `wwwroot` with `MapFallbackToFile("index.html")`, and `/` serves the SPA.

### Production deployment

Docker Compose stack on an ARM64 Jetson: `erda` + `whatsapp-bridge` containers. Codex auth is a bind-mounted `~/.codex` session. In `Production` (`ASPNETCORE_ENVIRONMENT=Production`), the interaction surfaces are WhatsApp and the LAN control panel (published on port 5167). The Dockerfile has a Node build stage that compiles the `web/` SPA and copies `dist` into `wwwroot`. Managed by Komodo (webhook → `docker compose up -d --build`).

## Configuration reference

**Env-only, no defaults** — no `appsettings.json`. Every setting is an environment variable
(`Section__Key` form), kept in `.env` (catalog: `.env.example`); `make dev` sources it, prod
`docker-compose` loads it via `env_file`. Options bind in `AddErdaCore`; **no setting has an in-code
default — required values are validated at startup** (`ValidateOnStart`) and a missing one stops the
app naming the key. Always-required: `CredentialsOptions` (flat `AZURE_OPENAI_*`/`OPENAI_API_KEY`,
`[Required]`) + all of `ErdaOptions` (`VaultPath`, `DbPath`, the model/codex settings — `[Required]` /
`[PositiveTimeSpan]`). Feature settings are required only when the feature's `Enabled` switch is on,
via per-feature `IValidateOptions` (`WhatsApp`/`Browser`/`ErrorWatch`/`Reminder` `OptionsValidator`).
Bool switches are off when absent (default-true behaviours like `AnalyzeWithCodex`/`NotifyOnError`/
`IngestToErda` are now switches you set in `.env`). The only non-config values are fixed mechanics
expressed as read-only constants on `BrowserOptions` (`McpCommand`, `McpArgs`, `MaxSteps`, `OpCommand`,
`OnePasswordVault`). Key settings:

| Section | Key | Purpose |
|---|---|---|
| (flat) | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `OPENAI_API_KEY` | Credentials (required, validated) |
| `Erda` | `VaultPath`, `DbPath`, `ChatDeployment`, `TranscribeModel`, `CodexModel`, `CodexReasoningEffort`, `CodexTimeout`, `CodexExecutable`, `VoiceMemoSubfolder` | All required (no default) — vault/db paths + model & codex settings |
| `WhatsApp` | `Enabled`, `OwnerNumber`, `BridgeUrl`, `SharedSecret`, `MediaTempDir` | Bridge integration (the four required when `Enabled`); only `OwnerNumber` is processed |
| `ErrorWatch` | `Enabled`, `PollInterval`, `MinLevel`, `MaxAlertsPerPoll`, `AnalyzeWithCodex` | Error-watch scheduler (interval/level/cap required when `Enabled`) |
| `Reminders` | `Enabled`, `NotePath`, `TimeZone`, `PollInterval`, `OverdueGrace`, `PreScript*` | Reminder scheduler (note/zone/intervals required when `Enabled`; pre-script limits when `PreScriptEnabled`) |
| `Seq` | `ServerUrl`, `ApiKey`, `IngestToErda` | Seq sink for Serilog + OTLP target (optional; blank ⇒ off) |
| `Observability` | `Enabled`, `CaptureMessageContent` | OTel master switch; content capture gate |
| `Erda:Browser` | `Enabled`, `ShowWindow`, `UserDataDir`, `OutputDir` | Agentic browser (`UserDataDir`/`OutputDir` required when `Enabled`; absent `ShowWindow` ⇒ headless) |
| `Panel` | `Username`, `Password` | Control-panel cookie login; blank `Password` = open (auth off) on the LAN |
