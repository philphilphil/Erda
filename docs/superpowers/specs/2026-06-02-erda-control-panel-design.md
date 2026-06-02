# Erda Control Panel — Design

- **Date:** 2026-06-02
- **Status:** Approved (design); pending implementation plan
- **Author:** Phil + Erda (Claude)

## Summary

Add a single-user **web control panel** to Erda for managing it directly — beyond what
WhatsApp and Obsidian offer. It is a **Blazor Server** UI hosted *inside* the existing Erda
.NET app (no separate frontend, no new container), mounted at `/panel`, bound to Phil's
**home LAN** only.

Four areas:

1. **Reminders** — view / add / edit / pause / cancel all reminders and scheduled prompts,
   with computed next-fire times.
2. **Prompt** — edit the system prompt with validation, preview, and **version history**
   (diff + rollback). Changes are hot-applied on the next agent turn.
3. **Activity** — a live (SignalR-pushed) feed of recent agent runs, tool calls, scheduled
   fires, and error-watch alerts.
4. **Config** — edit the runtime knobs (Codex effort, error-watch level, poll cadences, …);
   most apply live, a few show "restart required" + a one-click restart.

**All persistent state consolidates into a single SQLite database** (`/data/erda/erda.db`,
on a new bind-mounted volume): prompt versions, reminders + their run-state, error-watch
state, activity history, and config overrides. This **deletes** the markdown-table parser in
`ReminderStore` and the two JSON sidecars, gives one source of truth, and — as a side effect —
fixes a latent bug: today the sidecars live in the container's `LocalApplicationData` and are
wiped on every `docker compose up --build`. EF Core keeps a future SQLite→Postgres swap to a
provider change.

The Obsidian vault remains Erda's *content* store (notes, voice memos); it is no longer the
source of truth for reminders.

## Goals

- A LAN-reachable panel to manage reminders, the system prompt, runtime config, and to watch
  activity — usable from any device on the home network.
- One consolidated SQLite store for all machine/runtime state; survives redeploys.
- Edit the system prompt safely (validation + preview + versioned rollback) and have it take
  effect without a manual restart.
- Edit runtime config and have it apply live where feasible; clearly flag what needs a restart
  and offer a one-click restart.
- Keep the existing surfaces working unchanged: WhatsApp (live + scheduled), the `schedule_*`
  tools, the error-watch scheduler. The panel is additive.

## Non-goals (v1)

- **No remote/public exposure, no auth provider.** LAN-only; an optional single-field password
  is included but off by default. (If remote management becomes a need, the follow-up is to put
  the panel behind Tailscale — see *Out-of-scope follow-ups* — not to add public auth.)
- **No Postgres.** SQLite only. (EF Core makes the swap cheap later if a real multi-client need
  appears.)
- No multi-user, no roles, no per-reminder timezone, no editing of secrets/credentials from the
  panel (those stay in `.env`).
- No reminders in the vault anymore (moved to the DB). One-time import of an existing reminders
  note is supported; ongoing vault round-trip is dropped.

## Trade-off accepted

Moving reminders out of the vault removes hand-editing/browsing them in **Obsidian from the
phone while away from home**. Remote paths become: (a) WhatsApp `schedule_*` tools
(create/cancel from anywhere — unchanged), and (b) the LAN panel (at home only). Accepted for
v1; revisit via Tailscale if remote browsing/editing is wanted.

## Architecture

The panel is part of the existing app. New components, grouped by concern:

| Component | Namespace | Role |
|---|---|---|
| `ErdaDbContext` (EF Core, SQLite) | `Erda.Data` | The single DB; DbSets below; migrations. |
| `PromptVersion`, `ReminderRow`, `ErrorWatchRow`, `ActivityEntry`, `ConfigOverride` (entities) | `Erda.Data` | Table-mapped records. |
| `IPromptStore` / `PromptStore` | `Erda.Data` | Read/write prompt versions; cache active; raise `Changed`. |
| `IReminderRepository` / `ReminderRepository` | `Erda.Data` | Replaces the markdown `ReminderStore`. CRUD + run-state. |
| `IErrorWatchStateStore` (DB-backed) | `Erda.Data` | Replaces the JSON `ErrorWatchStateStore`. |
| `IActivityRecorder` / `ActivityRecorder` | `Erda.Services` | Append + bounded prune + live notify (`IObservable`/event). |
| `ErdaAgentProvider` | `Erda.Agents` | Builds/holds the current `AIAgent` from the active prompt; rebuilds on change. |
| `SqliteConfigurationSource`/`Provider` | `Erda.Configuration` | Layers `ConfigOverride` rows over `appsettings`; reloadable. |
| Blazor components (`/Components/Panel/*.razor`) | `Erda.Panel` | Reminders / Prompt / Activity / Config pages + layout. |

### Data flow (prompt + config hot-apply)

```
Panel (Blazor, LAN) ── edit prompt ──► PromptStore.SaveNewVersion ──► PromptVersions table
                                              │ raises Changed
                                              ▼
                                       ErdaAgentProvider rebuilds AIAgent
                                              │
WhatsApp turn ── ErdaAgentResponder.RespondAsync ──► provider.Current (new prompt; session reset)

Panel ── edit config ──► ConfigOverrides table ──► SqliteConfigurationProvider.Reload()
                                              │ change token fires
                                              ▼
                             IOptionsMonitor<T>.CurrentValue (read per tick/call by consumers)
```

## Data model (SQLite)

One DB file. Path from config `Erda:DbPath` (default `/data/erda/erda.db` in the container,
`LocalApplicationData/erda/erda.db` in dev). EF Core migrations create/upgrade the schema at
startup (`db.Database.Migrate()`).

### `PromptVersions`
| Column | Type | Notes |
|---|---|---|
| `Id` | INTEGER PK | autoincrement |
| `Content` | TEXT | the full system prompt |
| `CreatedAtUtc` | TEXT (datetime) | when saved |
| `IsActive` | INTEGER (bool) | exactly one row true |
| `Note` | TEXT? | optional change note |

Save = insert new row, set it active, clear the previous active (transaction). Rollback = mark
an older row active (or insert a copy of it as the newest). The agent always loads the active row.

### `Reminders`
Mirrors today's `Reminder` record plus run-state (formerly the JSON sidecar):

| Column | Type | Notes |
|---|---|---|
| `Id` | TEXT PK | slug or derived id (kept from current scheme) |
| `Kind` | INTEGER | `ReminderKind` (Reminder / Prompt) |
| `When` | TEXT | datetime `yyyy-MM-dd HH:mm` or cron — unchanged semantics |
| `Text` | TEXT | message or prompt payload (no pipe-escaping needed anymore) |
| `Status` | INTEGER | `ReminderStatus` (Active / Paused / Done) |
| `LastFiredUtc` | TEXT? | recurring cadence (was `LastFiredUtc[id]`) |
| `Fired` | INTEGER (bool) | one-shot send-once backstop (was `FiredOneShotIds`) |

`WhenSpec` is still parsed from `When` at load (the parser stays); it is not stored.
All the existing scheduler logic (one-shot grace, recurring no-backfill, DST via Cronos) is
unchanged — only the *storage* moves from markdown+JSON to these columns.

### `ErrorWatchState`
Single row (`Id = 1`). `LastTimestampUtc TEXT?`, `SeenSignaturesJson TEXT`, `SeenEventIdsJson
TEXT` (the two bounded lists stored as JSON via an EF value converter; child tables are an
alternative if querying is wanted later). Same `Trim` bounds (500/500).

### `Activity`
| Column | Type | Notes |
|---|---|---|
| `Id` | INTEGER PK | autoincrement |
| `TimestampUtc` | TEXT | |
| `Kind` | TEXT | `agent_run` / `tool_call` / `scheduled_fire` / `error_alert` |
| `Summary` | TEXT | one-line, panel-displayable |
| `DetailJson` | TEXT? | optional structured detail (tokens, tools, target) |

Append-only; pruned to the most recent N (config `Panel:ActivityRetention`, default 1000).
Seq remains the durable, full-fidelity record; this table is the panel's fast local feed.

### `ConfigOverrides`
| Column | Type | Notes |
|---|---|---|
| `Key` | TEXT PK | config key in `Section:Key` form, e.g. `ErrorWatch:MinLevel` |
| `Value` | TEXT | string value (config is string-keyed) |

Read by `SqliteConfigurationProvider`, layered last so it overrides `appsettings.json`/env.

## The four screens

All under `/panel`, sharing a layout (nav + connection-status). Components are thin: all logic
lives in the stores/services above so it is unit-testable without rendering.

1. **Reminders** (`Reminders.razor`)
   - Two tables (Reminders, Scheduled prompts) from `IReminderRepository`, each row showing
     `when`, payload, status, and **computed next-fire** (reuse `WhenSpec`/Cronos against
     `Reminders:TimeZone`).
   - Add / edit (validate `when` with the same parser the tools use), pause/resume, cancel.
   - Edits go straight to the DB; the scheduler reads the same rows next tick. No file race.

2. **Prompt** (`PromptEditor.razor`)
   - Textarea editor for the active prompt; live token/char count; **preview**.
   - **Validate before save** (non-empty; optional length ceiling) so the agent can't be bricked.
   - **Version history** list with timestamps + notes; view diff vs active; **rollback**.
   - Save → `PromptStore.SaveNewVersion` → `ErdaAgentProvider` rebuilds → next turn uses it.

3. **Activity** (`Activity.razor`)
   - Live feed via Blazor Server's SignalR circuit: subscribes to `IActivityRecorder` and
     prepends new entries. Filter by kind. "Open in Seq" deep-link for full traces.

4. **Config** (`Config.razor`)
   - Form over the editable knobs (see *Config reload*). Each field tagged **live** or
     **restart required**. Writes a `ConfigOverrides` row and triggers reload.
   - **Restart Erda** button → `IHostApplicationLifetime.StopApplication()`; Docker
     `restart: unless-stopped` brings it back in ~seconds. Clean, no shell access needed.

## Prompt hot-reload mechanism (primary implementation risk)

Today the agent is a **keyed singleton** built once at startup
(`builder.AddAIAgent(ErdaAgent.Name, (sp,_) => ErdaAgent.Create(sp))`) with the system prompt
baked in via `chatClient.AsAIAgent(instructions: …)`; `ErdaAgentResponder` captures it in its
constructor and drives one long-lived `AgentSession`.

**Plan:** introduce `ErdaAgentProvider` (singleton) that builds the `AIAgent` from
`PromptStore.Active` and rebuilds it when `PromptStore.Changed` fires (swap the held reference).
`ErdaAgentResponder` resolves `provider.Current` **per turn** (instead of a ctor field) and
tracks the prompt version its `_session` was built against; when the version changes it resets
`_session` before the next turn (a fresh context is the correct behaviour when the agent's
contract changes). `RunOnceAsync` (scheduled prompts) always uses `provider.Current`.

**Spike first:** confirm in a throwaway test that (a) building a new `AIAgent` from the same
chat client at runtime works and (b) resetting the session picks up the new instructions.
**Fallback** if mid-run swap is awkward in MAF 1.8: rebuild agent + fresh session on save only
(live conversation context resets on a prompt change — infrequent, acceptable). The OpenAI/DevUI
endpoints (`AddAIAgent`) are dev-only-meaningful and may keep the startup instance in v1.

## Config reload mechanism

Two parts:

1. **Source:** `SqliteConfigurationSource`/`Provider` added to `builder.Configuration`, loading
   `ConfigOverrides` rows (keys already `Section:Key`). Layered after appsettings/env so it wins.
   After the panel writes a row it calls the provider's reload to fire the change token.
2. **Consumers:** refactor the knob-readers from `IOptions<T>` to **`IOptionsMonitor<T>`** and
   read `.CurrentValue` at point of use (per tick / per call):
   - `ErrorWatchScheduler`, `ReminderScheduler` — read `CurrentValue` each tick (today they
     snapshot `.Value` once at loop start; change that).
   - `CodexRunner` — read `CurrentValue` per call (Codex effort default, model).

**Live vs restart-required:**
- **Live:** Codex effort default, `ErrorWatch:MinLevel` / `MaxAlertsPerPoll`, `Reminders`/
  `ErrorWatch` `NotifyOnError`, and (with a per-tick re-read + timer recreate) poll intervals.
- **Restart required:** chat **model/deployment** (baked into the chat client at startup), the
  **LAN bind port**, and anything read only at construction. The Config screen flags these and
  the Restart button handles them.

## Access & security

- The app already listens on `:5167`; the container currently publishes **no** host port. Add a
  published mapping bound to the LAN so `/panel` is reachable at `http://<jetson-lan-ip>:5167/panel`.
- **LAN-only**, as chosen. Optional `Panel:Password` (single shared secret, cookie session) —
  **off by default**; when set, a minimal login gate guards `/panel`.
- The OpenAI/DevUI transport endpoints stay dev-gated as today; only `/panel` is added in
  Production. `/` redirects to `/panel` in Production (to `/devui` in Development).

## Deployment changes (`docker-compose.yml`)

- **Publish the panel port** on the `erda` service, scoped to the LAN, e.g.
  `ports: ["${PANEL_BIND:-192.168.x.y}:5167:5167"]` (host LAN IP, not `0.0.0.0`, to avoid
  exposing beyond the LAN). Document `PANEL_BIND` in `.env.example`.
- **Add a persistent data volume** for the DB: bind `${DATA_DIR}:/data/erda` (absolute host path,
  like the vault/codex mounts) so `erda.db` survives `docker compose up --build`.
- `Erda__DbPath: /data/erda/erda.db` env on the `erda` service.
- No new container; no new image.

## Migration from current stores

On first startup with the new schema (`db.Database.Migrate()` creates tables):

1. **Prompt:** seed `PromptVersions` with one active row = the current `ErdaAgent` prompt
   constant, so behaviour is unchanged. The constant becomes the *seed/default*, kept in code as
   a fallback if the table is empty.
2. **Reminders:** if a legacy reminders note exists at `Reminders:NotePath`, do a **one-time
   import** (reuse the existing markdown parser before it is deleted) → seed `Reminders` rows,
   then ignore the note thereafter. If absent, start empty.
3. **Error-watch state:** if the legacy JSON sidecar exists, import its watermark + seen lists
   once; else start fresh (first run sets the watermark to now, as today).

After migration, delete the markdown `ReminderStore`, the JSON `*StateStore` file classes, and
the `Reminders:NotePath` write paths; `ReminderTools` and both schedulers point at the DB.

## Error handling

- **DB unavailable / migration failure at startup** → log fatal and refuse to start (the DB is
  now core state). The bind mount makes this rare; a corrupt file is surfaced loudly, not
  silently reset.
- **Prompt save validation failure** → rejected in the UI; no new version written; agent
  unchanged.
- **Config write of an unknown/invalid key** → validated against a known-keys allowlist in the
  Config screen; bad values rejected before write.
- **Activity recorder** → best-effort; a recorder failure never breaks an agent turn or a
  scheduler tick (swallow + log), matching the current "telemetry must not break the path" stance.
- **Scheduler/tool DB writes** → wrapped like today's tick try/catch; a transient DB error
  retries next tick.

## Testing plan (xUnit, EF Core SQLite in-memory/file)

- **PromptStore** — save creates a new active version and deactivates the prior; rollback;
  active-content cache + `Changed` event fires.
- **ReminderRepository** — CRUD; status changes; run-state (`LastFiredUtc`, `Fired`) round-trip;
  next-fire computation surfaced. Re-point the existing `ReminderScheduler` tests at the repo
  (the scheduler logic/tests are unchanged; only the store seam swaps).
- **ErrorWatchState (DB)** — load/save/trim parity with the old JSON store; scheduler tests
  re-pointed.
- **SqliteConfigurationProvider** — overrides win over appsettings; reload fires the change
  token; `IOptionsMonitor.CurrentValue` reflects an edit.
- **ActivityRecorder** — append + bounded prune; subscribers notified.
- **ErdaAgentProvider** — rebuilds on `PromptStore.Changed`; responder resets session on version
  change (the prompt hot-reload spike graduates into a test).
- **Migration** — one-time import from a sample legacy note + sidecar seeds the DB correctly.
- Blazor components kept thin; logic tested via the services above (no component-render tests in
  v1).

## Out-of-scope follow-ups (noted, not built)

- **Tailscale exposure** of the panel for remote management (the natural answer if the LAN-only
  trade-off bites).
- Persisting full conversation history / a transcript browser in the panel.
- SQLite→Postgres if multi-client or web-scale needs ever appear (EF makes it a provider swap).
- Editing tool descriptions / per-tool toggles from the panel.
- Auth beyond the single optional password (real users/roles).
- Consolidating Seq-sourced metrics/dashboards into the Activity screen.
