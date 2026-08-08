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
- `Erda.Server/Api/` — control-panel JSON API, grouped by feature (`Reminders/`, `Prompts/`, `Activity/` incl. the `/api/activity/stream` SSE feed, `VoiceMemos/`, `Config/`, `Auth/`); `PanelApi` wires the groups; `CsrfEndpointFilter` (requires `X-Requested-With: erda-panel` on mutations); per-feature DTOs; `ReminderView`/`ConfigPanelService`/`PanelCredentials` hold panel logic
- `web/` — Vue 3 + Vite + TS control-panel SPA (5 routes + login). Dev: `make web` (Vite proxies `/api` to the backend). Prod: `npm run build` → `web/dist`, copied into `wwwroot` by the Dockerfile and served with `MapFallbackToFile("index.html")`
- `Erda.Agents/Orchestration/ErdaAgent.cs` — orchestrator: system prompt + tool registration (vault tools, `process_voice_memo`, `HostedWebSearchTool` for native web search)
- `Erda.Agents/Orchestration/ErdaAgentResponder.cs` — implements `Erda.Core.Abstractions.IAgentResponder`; adapts agent turns for the WhatsApp channel; streams via `RunStreamingAsync(...).ToAgentResponseAsync(ct)` (the proxy's non-streamed Responses is broken)
- `Erda.Agents/Tools/` — `ObsidianTools` (vault tools, confined to `VaultPath`), `ReminderTools`, `NotifyTools`
- `Erda.Core/Services/IReasoner.cs` + `Services/ResponsesReasoner.cs` — the in-process reasoning seam: `ResponsesReasoner` runs the streamed Responses API (optional `web_search`), collapsed to final text. Replaces the former `codex` CLI subprocess; used by voice-memo, recipe, error-watch, and reminders
- `Erda.Core/Services/VaultService.cs` — path-safe file I/O under `VaultPath`
- `Erda.Core/Services/Transcriber.cs` — OpenAI audio transcription
- `Erda.Agents/Workflows/VoiceMemoWorkflow.cs` — voice memo pipeline (transcribe → `IReasoner` → write); wrapped as `process_voice_memo` tool via `AsAIFunction`; implements `IMemoProcessor` via `MemoProcessor`
- `Erda.Core/Scheduling/ErrorWatch/ErrorWatchScheduler.cs` — background loop: polls Seq for errors, deduplicates by signature, analyzes via `IReasoner`, alerts via WhatsApp
- `Erda.Core/WhatsApp/` — bridge integration: inbound queue, background worker, channel service (dispatches text/voice/image to the agent), sender; the HTTP endpoint is `Erda.Server/WhatsApp/WhatsAppEndpoints.cs`
- `Erda.Core/Upload/UploadIntake.cs` + `Erda.Server/Upload/UploadEndpoints.cs` — `POST /upload`: a bearer-authenticated audio upload (iOS Shortcut) accepting either a **raw body** (Shortcut "Request Body: File") or `multipart/form-data` with a field named `audio`; a raw body carries no filename, so an optional `X-Filename` header supplies the display name. The file is saved and enqueued onto the WhatsApp inbound queue, so it runs the **same** Apple-Voice-Memo pipeline (transcribe → `IReasoner` → `1 Inbox/`) and replies over WhatsApp. Returns `202` immediately; gated by `Upload:Enabled` and requires `WhatsApp:Enabled`
- `Erda.Core/Services/VoiceMemoArchive.cs` — durable archive of **all** inbound voice audio, tagged by `VoiceMemoSource`: `upload` (recorded by `UploadIntake`), `apple-memo` and `whatsapp-voice` (both recorded by `WhatsAppChannelService`, after the replay/owner/dev-prefix gates). The audio is copied to a `voice-archive/` directory beside the SQLite DB — durable, untouched by the per-turn media cleanup — plus a `VoiceMemos` row holding date/filename/source and what it produced: the note path for `filed`/`raw` memos, the transcript for an `answered` agent turn (which writes no note). Source and status persist as lowercase-kebab TEXT via EF value conversions and reach the panel as the same strings. Rows left `pending` by a dead process are swept to `failed` at startup. Panel surface: `GET /api/voice-memos`, `GET /api/voice-memos/{id}/audio` (range-enabled, for `<audio>` playback), `DELETE /api/voice-memos/{id}` (drops the row + audio, never the note) behind the `/voice-memos` SPA route
- `macos-bridge/` — **ErdaBridge**, a separate Swift Package: a signed, hardened-runtime macOS menu-bar app that exposes a small LAN HTTP API (bearer-token auth) so Erda can create/list/complete Apple Reminders via EventKit. A list is addressed by its **real name** as it reads in Reminders.app (`{"list":"Groceries",…}`, `?list=…`); there is no allowlist and no alias table — it was removed deliberately, since macOS grants reminder access all-or-nothing and Phil decided reaching all of his own lists is what he wants, so the bridge **can read and write every reminder list on that Mac** (see its README's threat model). Names resolve against EventKit per request: exact match, else a *unique* case-insensitive match; no match or an ambiguous one is `no_such_list` (404), a read-only list is `list_read_only` (409), and `complete` re-reads the reminder's current list so a re-homed id can't quietly succeed. The same bridge also does **Apple Calendar**, but **reads and writes are deliberately asymmetric**. Reads span every calendar and may still name one (`?calendar=…`), with a name matching nothing `no_such_calendar` (404) and a name matching **two** `ambiguous_calendar` (409) — split rather than folded, because Erda relays the reason to Phil and the two fixes differ. **Writes are pinned to one calendar** chosen in the bridge's Setup window: `POST /v1/calendar-events` carries **no `calendar` key** (strict decoding makes a stale client's `calendar` a clean 400), and the target is stored in `meta` as `calendar_id` + `calendar_title` and resolved by **identifier** on every write — so a rename is a no-op, while a deleted calendar is `calendar_not_configured` (503, a new code) rather than a re-bind onto whatever now wears the title. Never configured and no-longer-resolvable share that one code, since both are fixed the same way: pick one in the ErdaBridge window. It is deliberately distinct from `calendar_unavailable` (503, the macOS grant → System Settings) and from a transport failure ("your Mac is unreachable"). `GET /v1/status` reports `writeCalendar: {state: ok|not_configured|unresolvable, name?}`; no route can set it. A pinned calendar that became unwritable is `calendar_read_only` (409). **TCC grants take effect without an app restart**, which needed two fixes and is easy to regress: `requestFullAccessTo*` can return `granted=true` while `EKEventStore.authorizationStatus(for:)` still says `notDetermined` (tccd answers asynchronously), so `GrantSettling` re-reads for up to 2s and `GrantNote` lets a `granted=true` cover a reported `notDetermined` for 30s — **only** `notDetermined`, so a revocation is never masked; and the actor's long-lived `EKEventStore` caches the old grant, so `EventKitStore.adoptGrant` calls `store.reset()` when a grant becomes usable. The setup UI and `/v1/status` both read `RemindersAccess.status()`/`CalendarAccess.status()`, so they cannot disagree. Note `authorizationStatus(for:)` is a **synchronous XPC call to CalendarDaemon**, not a local read — each request path reads it once and passes the result down. Runs on Phil's Mac, outside the Docker stack; has its own build (`make bundle`/`make test`) and README. `Erda.Core/Services/AppleBridgeClient.cs` (`IAppleBridgeClient`) is the .NET client for both halves — never throws, like `WhatsAppSender`; every method must `await` the send **inside** its `using var request` scope (a returned-but-unawaited Task disposes the body first and surfaces as a bogus "Mac unreachable" — there are regression tests for both POSTs). `Erda.Agents/Tools/AppleReminderTools.cs` exposes `create_apple_reminder`/`list_apple_reminders`/`complete_apple_reminder` and `Erda.Agents/Tools/AppleCalendarTools.cs` exposes `create_calendar_event`/`list_calendar_events`; both are registered on the agent under the single `AppleBridge:Enabled` switch. `create_calendar_event` has **no `calendar` parameter** — a parameter that does not exist is the only "never guess" that cannot be argued around — while `list_calendar_events` keeps its optional filter. Deliberately distinct from `ReminderTools`' `schedule_*`/`list_scheduled`, which are Erda's own DB-backed WhatsApp scheduler.
  - **Calendar specifics.** Two operations only — create an event, list upcoming ones (window starts *now*, `?days=` 1–31 default 7, `?limit=` ≤ 200 default 50). **No edit, no delete**, no recurrence, no attendees, no alarms; an event carries **no id** on the wire because no route takes one. An **all-day** event alone additionally carries `startDay`/`endDay` (`yyyy-MM-dd`, the *inclusive* last day), stated by the Mac because such an event is **floating**: EventKit anchors it to the Mac's own zone and leaves `timeZone` nil, so the instants lose the anchoring zone — `2026-08-10T22:00:00Z` is a birthday Calendar.app draws on the 11th, and any client deriving a day from that reports every birthday one day early. `AppleCalendarTools.FormatRange` therefore renders an all-day event from those days (naming both ends when they differ) and falls back to the instant only for a bridge too old to send them. `startAt`/`endAt` need an explicit UTC offset, end strictly after start, ≤ 7 days; optional `timeZone` must be a *canonical* IANA identifier (`CEST`/`PST`/`GMT+2` are refused — they parse but are ambiguous; `UTC` canonicalises to `GMT`). The bridge holds **Calendars full access, not write-only**, because resolving a calendar by title requires enumerating calendars — accepted cost: it can read every event on that Mac (bridge README's threat model). Reminders and Calendar authorization are **independent** (separate TCC records, separate 503s `reminders_unavailable`/`calendar_unavailable`); denying one never disables the other. Inside the bridge one actor, `EventKitStore`, implements both `RemindersService` and `CalendarService` over a **single `EKEventStore`** — a second store would be a second uncoordinated writer, and sharing one across two actors can't be expressed in Swift 6 without an `@unchecked Sendable` hole. `GET /v1/status` reports both capabilities separately (`availability`/`lists` + `calendarAvailability`/`calendars`). The JSONL audit log records the list/calendar *name* and nothing else: **no event titles, notes or times** — `AuditEvent` has no bare `String` and no `Date` beyond the request timestamp, so that is structural.

### MAF-specific patterns

- Chat agent: `new OpenAI.Responses.ResponsesClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(chatBaseUrl) }).AsAIAgent(model: ChatModel, ...)` (the Responses construction block needs `#pragma warning disable OPENAI001`). The stock **OpenAI SDK** (`OpenAI`/`OpenAI.Responses`) pointed at the local OpenAI-compatible endpoint — **not** `Azure.AI.OpenAI`. Uses `System.ClientModel.ApiKeyCredential`. We deliberately use the **Responses API, not Chat Completions** — it's the only surface on this endpoint that supports native `web_search` (via `HostedWebSearchTool`), and only in **streaming mode** (the proxy's non-streamed Responses returns empty output). This drops the former "Chat Completions for portability" stance: we're committing to this endpoint.
- Workflow-as-tool: the voice-memo workflow is `workflow.AsAIAgent(...).AsAIFunction(...)`. Its start executor must accept `List<ChatMessage>` + `TurnToken` (a plain `string` start executor fails with "Workflow does not support ChatProtocol").
- **Agent `name` matches its registration key** (both `"erda"`): registered with `builder.AddAIAgent(ErdaAgent.Name, …)` and resolved by keyed DI via `[FromKeyedServices("erda")]` in `ErdaAgentResponder` and `WebChatService`.

### Observability

OpenTelemetry traces exported over OTLP to Seq (`{Seq:ServerUrl}/ingest/otlp/v1/traces`). Every span is tagged `app = Erda` — filter `app = 'Erda'` in Seq (not `service.name`, which lands under `@ra` and is not filterable). Content capture (prompts, tool args) is off in production, on in Development.

### WhatsApp channel

The `whatsapp-bridge` (Go) handles the WhatsApp socket and posts inbound messages to `POST /whatsapp/inbound`. `WhatsAppInboundWorker` drains the queue and calls `WhatsAppChannelService`, which enforces the owner whitelist and dispatches by message type. The bridge and Erda share a `/media` Docker volume for downloaded audio/images. Inbound **audio** (both the Apple-memo and the conversational branch) is copied into the voice-memo archive before it is processed; text and images are not. The bridge also exposes `POST /presence` (→ `SendChatPresence`); `WhatsAppChannelService` drives a typing indicator (`"composing"` before the turn, `"paused"` after) around the streamed reply.

### Control panel (Vue SPA + JSON API)

A single-user, LAN-only web UI replaces the former Blazor Server panel. The backend exposes a JSON API under `/api/*` (minimal-API groups in `Api/`) over the same DB-backed services; the frontend is a Vue 3 SPA in `web/`. **Reminders are live** (the scheduler reads the DB each tick) and **prompt edits apply on restart** (`POST /api/config/restart` → `IHostApplicationLifetime.StopApplication()`; Docker `restart: unless-stopped` brings it back). **Config is env-only and the Config page is read-only** — it surfaces the effective loaded values (secrets masked); to change a setting, edit `.env` and restart. Live activity is pushed over **SSE** (`GET /api/activity/stream`), bridging `IActivityRecorder.Recorded`. Auth is **cookie-based, off by default** — open on the LAN unless `Panel__Password` is set; CSRF is guarded by `SameSite=Lax` + a required `X-Requested-With: erda-panel` header on mutations (no `Secure` flag, since the panel is plain-HTTP on the LAN). Dev: Vite (`:5173`) proxies `/api` to the backend (`:5167`); prod: the Vite build is served from `wwwroot` with `MapFallbackToFile("index.html")`, and `/` serves the SPA.

### Production deployment

Self-contained Docker Compose stack on an amd64 homeserver: `erda` + `whatsapp-bridge` + `obsidian-sync` containers. The chat/reasoning model is reached over HTTP at `Erda__ChatBaseUrl` (a local OpenAI-compatible proxy), so the container needs no codex CLI or `~/.codex` mount. All persistent state is held in **Docker-managed named volumes** (`vault`, `erda-data`, `media`, `bridge-data`, `obsidian-config`, at `/var/lib/docker/volumes/erda_<name>/_data` — backed up directly); they are created root-owned and chowned to `1000:1000` by the `init-perms` one-shot on first `up`. The vault is kept synced **inside the stack** by the `obsidian-sync` sidecar — Obsidian's official headless Sync client (`obsidian-headless`, Node 22+); auth is injected via `OBSIDIAN_AUTH_TOKEN` or a one-time `docker compose run --rm obsidian-sync setup` that persists into the `obsidian-config` dir (no host-side Syncthing/obsidian-git anymore; requires an Obsidian Sync subscription). In `Production` (`ASPNETCORE_ENVIRONMENT=Production`), the interaction surfaces are WhatsApp and the LAN control panel (published on port 5167). The Dockerfile has a Node build stage that compiles the `web/` SPA and copies `dist` into `wwwroot`.

**Images are built by CI, not on the server.** `.github/workflows/build.yml` (push to `main`, `v*` tags, or manual dispatch) builds all three images for `linux/amd64` and pushes them to GHCR as `ghcr.io/philphilphil/{erda,whatsapp-bridge,obsidian-sync}` (`latest` on `main`, plus `sha-<short>` and semver tags on `v*`). The server runs compose + `.env` only — no source checkout, no build — and pulls those prebuilt images: `make deploy` is now `docker compose pull && docker compose up -d` (Komodo runs the same). The compose `image:` refs point at the GHCR `:latest` tags; the `build:` blocks are kept **only** so local dev still works with `docker compose up -d --build`. One-time setup done outside this repo: make the 3 GHCR packages public (anonymous pulls) or give the server/Komodo a `read:packages` login; and point a Komodo **Stack** at this compose with env managed in Komodo, redeploying via webhook or the Komodo API.

## Configuration reference

**Env-only, no defaults** — no `appsettings.json`. Every setting is an environment variable
(`Section__Key` form), kept in `.env` (catalog: `.env.example`); `make dev` sources it, prod
`docker-compose` loads it via `env_file`. Options bind in `AddErdaCore`; **no setting has an in-code
default — required values are validated at startup** (`ValidateOnStart`) and a missing one stops the
app naming the key. Always-required: `CredentialsOptions` (flat `OPENAI_API_KEY`, `[Required]`) + all
of `ErdaOptions` (`VaultPath`, `DbPath`, the chat-endpoint/model settings — `[Required]`; optional
`ChatApiKey` defaults to `"local"`). Feature settings are required only when the feature's `Enabled` switch is on,
via per-feature `IValidateOptions` (`WhatsApp`/`Upload`/`ErrorWatch`/`Reminder`/`AppleBridge`
`OptionsValidator`). Bool switches are off when absent (default-true behaviours like
`AnalyzeWithCodex`/`NotifyOnError`/`IngestToErda` are now switches you set in `.env`). Key settings:

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
| `AppleBridge` | `Enabled`, `BaseUrl`, `ApiKey`, `TimeoutSeconds` | macOS ErdaBridge client (`BaseUrl`/`ApiKey`/`TimeoutSeconds` required when `Enabled`) — Apple Reminders create/list/complete **and** Apple Calendar create/list tools; one switch for both, see `macos-bridge/` |
| `Panel` | `Username`, `Password` | Control-panel cookie login; blank `Password` = open (auth off) on the LAN |
