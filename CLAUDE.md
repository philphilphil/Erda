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

- **`Erda.Core`** (`Microsoft.NET.Sdk`) — host-agnostic business logic: `Configuration/`, `Data/` (EF + migrations), `Services/` (Vault, Reasoner, Transcriber, ActivityRecorder, clock, `Seq/`), `Scheduling/` (`Reminders/` + `ErrorWatch/`), `WhatsApp/` (channel/sender/queue/worker), and `Abstractions/` (the `IAgentResponder`/`IMemoProcessor` seams that keep Core free of any MAF/ASP.NET dependency). `AddErdaCore()` wires it all.
- **`Erda.Agents`** (`Microsoft.NET.Sdk`) — the MAF layer: `Orchestration/` (the `erda` agent + responder), `Tools/`, `Workflows/`. `AddErdaAgents()` wires it.
- **`Erda.Server`** (`Microsoft.NET.Sdk.Web`) — the only runnable app: `Program.cs`, `Api/`, `WhatsApp/WhatsAppEndpoints.cs`, `Hosting/`. Serves the SPA from `wwwroot`.
- **`Erda.Tests`** (xUnit) — references all three.

### The two-credential model

| Capability | Client | Key |
|---|---|---|
| Chat agent (`gpt-5.5`) | OpenAI SDK → local OpenAI-compatible endpoint (`OpenAI.Responses.ResponsesClient` + Responses API, streamed, with `HostedWebSearchTool`) | `Erda__ChatBaseUrl` + model `Erda__ChatModel` + optional `Erda__ChatApiKey` (default `"local"`) |
| Transcription (`gpt-4o-transcribe`) | OpenAI SDK | `OPENAI_API_KEY` |

The chat agent runs `gpt-5.5` on a local OpenAI-compatible endpoint via the **Responses API in streaming mode** (the proxy's non-streamed Responses returns empty output), which also exposes native web search through a `HostedWebSearchTool`. Transcription still uses `OPENAI_API_KEY` (the endpoint has no transcribe model).

### Key source files

- `Erda.Server/Program.cs` — host wiring: Serilog, OpenTelemetry, then `AddErdaCore()` + `AddErdaAgents()` + `AddPanelApi()`, the agent registration, and the request pipeline (SPA static hosting + `/api` + WhatsApp endpoint)
- `Erda.Server/Api/` — control-panel JSON API, grouped by feature (`Reminders/`, `Prompts/`, `Activity/` incl. the `/api/activity/stream` SSE feed, `Config/`, `Auth/`); `PanelApi` wires the groups; `CsrfEndpointFilter` (requires `X-Requested-With: erda-panel` on mutations); per-feature DTOs; `ReminderView`/`ConfigPanelService`/`PanelCredentials` hold panel logic
- `web/` — Vue 3 + Vite + TS control-panel SPA (4 routes + login). Dev: `make web` (Vite proxies `/api` to the backend). Prod: `npm run build` → `web/dist`, copied into `wwwroot` by the Dockerfile and served with `MapFallbackToFile("index.html")`
- `Erda.Agents/Orchestration/ErdaAgent.cs` — orchestrator: system prompt + tool registration (vault tools, `process_voice_memo`, `HostedWebSearchTool` for native web search)
- `Erda.Agents/Orchestration/ErdaAgentResponder.cs` — implements `Erda.Core.Abstractions.IAgentResponder`; adapts agent turns for the WhatsApp channel; streams via `RunStreamingAsync(...).ToAgentResponseAsync(ct)` (the proxy's non-streamed Responses is broken)
- `Erda.Agents/Tools/` — `ObsidianTools` (vault tools, confined to `VaultPath`), `ReminderTools`, `NotifyTools`
- `Erda.Core/Services/IReasoner.cs` + `Services/ResponsesReasoner.cs` — the in-process reasoning seam: `ResponsesReasoner` runs the streamed Responses API (optional `web_search`), collapsed to final text. Replaces the former `codex` CLI subprocess; used by voice-memo, recipe, error-watch, and reminders
- `Erda.Core/Services/VaultService.cs` — path-safe file I/O under `VaultPath`
- `Erda.Core/Services/Transcriber.cs` — OpenAI audio transcription
- `Erda.Agents/Workflows/VoiceMemoWorkflow.cs` — voice memo pipeline (transcribe → `IReasoner` → write); wrapped as `process_voice_memo` tool via `AsAIFunction`; implements `IMemoProcessor` via `MemoProcessor`
- `Erda.Core/Scheduling/ErrorWatch/ErrorWatchScheduler.cs` — background loop: polls Seq for errors, deduplicates by signature, analyzes via `IReasoner`, alerts via WhatsApp
- `Erda.Core/WhatsApp/` — bridge integration: inbound queue, background worker, channel service (dispatches text/voice/image to the agent), sender; the HTTP endpoint is `Erda.Server/WhatsApp/WhatsAppEndpoints.cs`
- `Erda.Core/Upload/UploadIntake.cs` + `Erda.Server/Upload/UploadEndpoints.cs` — `POST /upload`: a bearer-authenticated audio upload (iOS Shortcut) accepting either a **raw body** (Shortcut "Request Body: File") or `multipart/form-data` with a field named `audio`. The file is saved and enqueued onto the WhatsApp inbound queue, so it runs the **same** Apple-Voice-Memo pipeline (transcribe → `IReasoner` → `1 Inbox/`) and replies over WhatsApp. Returns `202` immediately; gated by `Upload:Enabled` and requires `WhatsApp:Enabled`

### MAF-specific patterns

- Chat agent: `new OpenAI.Responses.ResponsesClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(chatBaseUrl) }).AsAIAgent(model: ChatModel, ...)` (the Responses construction block needs `#pragma warning disable OPENAI001`). The stock **OpenAI SDK** (`OpenAI`/`OpenAI.Responses`) pointed at the local OpenAI-compatible endpoint — **not** `Azure.AI.OpenAI`. Uses `System.ClientModel.ApiKeyCredential`. We deliberately use the **Responses API, not Chat Completions** — it's the only surface on this endpoint that supports native `web_search` (via `HostedWebSearchTool`), and only in **streaming mode** (the proxy's non-streamed Responses returns empty output). This drops the former "Chat Completions for portability" stance: we're committing to this endpoint.
- Workflow-as-tool: the voice-memo workflow is `workflow.AsAIAgent(...).AsAIFunction(...)`. Its start executor must accept `List<ChatMessage>` + `TurnToken` (a plain `string` start executor fails with "Workflow does not support ChatProtocol").
- **Agent `name` matches its registration key** (both `"erda"`): registered with `builder.AddAIAgent(ErdaAgent.Name, …)` and resolved by keyed DI via `[FromKeyedServices("erda")]` in `ErdaAgentResponder` and `WebChatService`.

### Observability

OpenTelemetry traces exported over OTLP to Seq (`{Seq:ServerUrl}/ingest/otlp/v1/traces`). Every span is tagged `app = Erda` — filter `app = 'Erda'` in Seq (not `service.name`, which lands under `@ra` and is not filterable). Content capture (prompts, tool args) is off in production, on in Development.

### WhatsApp channel

The `whatsapp-bridge` (Go) handles the WhatsApp socket and posts inbound messages to `POST /whatsapp/inbound`. `WhatsAppInboundWorker` drains the queue and calls `WhatsAppChannelService`, which enforces the owner whitelist and dispatches by message type. The bridge and Erda share a `/media` Docker volume for downloaded audio/images. The bridge also exposes `POST /presence` (→ `SendChatPresence`); `WhatsAppChannelService` drives a typing indicator (`"composing"` before the turn, `"paused"` after) around the streamed reply.

### Control panel (Vue SPA + JSON API)

A single-user, LAN-only web UI replaces the former Blazor Server panel. The backend exposes a JSON API under `/api/*` (minimal-API groups in `Api/`) over the same DB-backed services; the frontend is a Vue 3 SPA in `web/`. **Reminders are live** (the scheduler reads the DB each tick) and **prompt edits apply on restart** (`POST /api/config/restart` → `IHostApplicationLifetime.StopApplication()`; Docker `restart: unless-stopped` brings it back). **Config is env-only and the Config page is read-only** — it surfaces the effective loaded values (secrets masked); to change a setting, edit `.env` and restart. Live activity is pushed over **SSE** (`GET /api/activity/stream`), bridging `IActivityRecorder.Recorded`. Auth is **cookie-based, off by default** — open on the LAN unless `Panel__Password` is set; CSRF is guarded by `SameSite=Lax` + a required `X-Requested-With: erda-panel` header on mutations (no `Secure` flag, since the panel is plain-HTTP on the LAN). Dev: Vite (`:5173`) proxies `/api` to the backend (`:5167`); prod: the Vite build is served from `wwwroot` with `MapFallbackToFile("index.html")`, and `/` serves the SPA.

### Production deployment

Self-contained Docker Compose stack on an amd64 homeserver: `erda` + `whatsapp-bridge` + `obsidian-sync` containers (the arch default is an amd64 build ARG — `OP_ARCH` — overridable for ARM64). The chat/reasoning model is reached over HTTP at `Erda__ChatBaseUrl` (a local OpenAI-compatible proxy), so the container needs no codex CLI or `~/.codex` mount. All persistent state is held in **Docker-managed named volumes** (`vault`, `erda-data`, `media`, `browser-data`, `bridge-data`, `obsidian-config`, at `/var/lib/docker/volumes/erda_<name>/_data` — backed up directly); they are created root-owned and chowned to `1000:1000` by the `init-perms` one-shot on first `up`. The vault is kept synced **inside the stack** by the `obsidian-sync` sidecar — Obsidian's official headless Sync client (`obsidian-headless`, Node 22+); auth is injected via `OBSIDIAN_AUTH_TOKEN` or a one-time `docker compose run --rm obsidian-sync setup` that persists into the `obsidian-config` dir (no host-side Syncthing/obsidian-git anymore; requires an Obsidian Sync subscription). In `Production` (`ASPNETCORE_ENVIRONMENT=Production`), the interaction surfaces are WhatsApp and the LAN control panel (published on port 5167). The Dockerfile has a Node build stage that compiles the `web/` SPA and copies `dist` into `wwwroot`.

**Images are built by CI, not on the server.** `.github/workflows/build.yml` (push to `main`, `v*` tags, or manual dispatch) builds all three images for `linux/amd64` and pushes them to GHCR as `ghcr.io/philphilphil/{erda,whatsapp-bridge,obsidian-sync}` (`latest` on `main`, plus `sha-<short>` and semver tags on `v*`). The server runs compose + `.env` only — no source checkout, no build — and pulls those prebuilt images: `make deploy` is now `docker compose pull && docker compose up -d` (Komodo runs the same). The compose `image:` refs point at the GHCR `:latest` tags; the `build:` blocks are kept **only** so local dev still works with `docker compose up -d --build`. One-time setup done outside this repo: make the 3 GHCR packages public (anonymous pulls) or give the server/Komodo a `read:packages` login; and point a Komodo **Stack** at this compose with env managed in Komodo, redeploying via webhook or the Komodo API.

## Configuration reference

**Env-only, no defaults** — no `appsettings.json`. Every setting is an environment variable
(`Section__Key` form), kept in `.env` (catalog: `.env.example`); `make dev` sources it, prod
`docker-compose` loads it via `env_file`. Options bind in `AddErdaCore`; **no setting has an in-code
default — required values are validated at startup** (`ValidateOnStart`) and a missing one stops the
app naming the key. Always-required: `CredentialsOptions` (flat `OPENAI_API_KEY`, `[Required]`) + all
of `ErdaOptions` (`VaultPath`, `DbPath`, the chat-endpoint/model settings — `[Required]`; optional
`ChatApiKey` defaults to `"local"`). Feature settings are required only when the feature's `Enabled` switch is on,
via per-feature `IValidateOptions` (`WhatsApp`/`Browser`/`ErrorWatch`/`Reminder` `OptionsValidator`).
Bool switches are off when absent (default-true behaviours like `AnalyzeWithCodex`/`NotifyOnError`/
`IngestToErda` are now switches you set in `.env`). The only non-config values are fixed mechanics
expressed as read-only constants on `BrowserOptions` (`McpCommand`, `McpArgs`, `MaxSteps`, `OpCommand`,
`OnePasswordVault`). Key settings:

| Section | Key | Purpose |
|---|---|---|
| (flat) | `OPENAI_API_KEY` | Transcription credential (required, validated) |
| `Erda` | `VaultPath`, `DbPath`, `ChatBaseUrl`, `ChatModel`, `ChatReasoningEffort`, `ChatApiKey`, `TranscribeModel`, `VoiceMemoSubfolder` | Required (no default) except `ChatApiKey` (optional, default `"local"`) — vault/db paths + local chat-endpoint/model settings |
| `WhatsApp` | `Enabled`, `OwnerNumber`, `BridgeUrl`, `SharedSecret`, `MediaTempDir` | Bridge integration (the four required when `Enabled`); only `OwnerNumber` is processed |
| `Upload` | `Enabled`, `ApiKey`, `MaxUploadMb` | `POST /upload` HTTP audio intake → same voice-memo pipeline (`ApiKey`/`MaxUploadMb` required when `Enabled`; 50 MB recommended; requires `WhatsApp:Enabled`) |
| `ErrorWatch` | `Enabled`, `PollInterval`, `MinLevel`, `MaxAlertsPerPoll`, `AnalyzeWithCodex`, `ReAlertAfter`, `SignatureProperties` | Error-watch scheduler (interval/level/cap required when `Enabled`; `ReAlertAfter` re-alerts an ongoing error after a cooldown, absent ⇒ once-ever; `SignatureProperties` folds named properties into the dedup signature for constant-template events) |
| `Reminders` | `Enabled`, `TimeZone`, `PollInterval`, `OverdueGrace`, `PreScript*` | Reminder scheduler (zone/intervals required when `Enabled`; pre-script limits when `PreScriptEnabled`) |
| `Seq` | `ServerUrl`, `ApiKey`, `IngestToErda` | Seq sink for Serilog + OTLP target (optional; blank ⇒ off) |
| `Observability` | `Enabled`, `CaptureMessageContent` | OTel master switch; content capture gate |
| `Erda:Browser` | `Enabled`, `ShowWindow`, `UserDataDir`, `OutputDir` | Agentic browser (`UserDataDir`/`OutputDir` required when `Enabled`; absent `ShowWindow` ⇒ headless) |
| `Panel` | `Username`, `Password` | Control-panel cookie login; blank `Password` = open (auth off) on the LAN |
