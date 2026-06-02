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
   (diff + rollback). Changes **apply on the next restart** (one-click restart button) — no
   live hot-swap in v1.
3. **Activity** — a live (SignalR-pushed) feed of recent agent runs, tool calls, scheduled
   fires, and error-watch alerts.
4. **Config** — edit the runtime knobs (Codex effort, error-watch level, poll cadences, …);
   changes **apply on restart** (one-click restart button). No live reload in v1.

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
- Edit the system prompt safely (validation + preview + versioned rollback) and apply it with a
  one-click restart.
- Edit runtime config and apply it with a one-click restart.
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
| `SqliteConfigurationSource`/`Provider` | `Erda.Configuration` | Read-once: layers `ConfigOverride` rows over `appsettings` at startup. |
| Blazor components (`/Components/Panel/*.razor`) | `Erda.Panel` | Reminders / Prompt / Activity / Config pages + layout. |

### Data flow (prompt + config apply on restart)

```
Panel (Blazor, LAN) ── edit prompt  ──► PromptStore.SaveNewVersion ──► PromptVersions table
Panel               ── edit config  ──► ConfigOverrides table
Panel               ── Restart Erda ──► IHostApplicationLifetime.StopApplication()
                                              │  Docker restart: unless-stopped
                                              ▼
   startup: ErdaAgent.Create reads active PromptVersion; SqliteConfigurationProvider loads
            ConfigOverrides into IConfiguration; IOptions<T> bind as today.

Reminders are live without restart: ReminderScheduler reads the DB every tick, so panel edits
take effect on the next poll. The Activity feed is pushed live over the Blazor Server circuit.
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
   - Form over the editable knobs (see *Config application*). Writes a `ConfigOverrides` row;
     a banner notes changes apply on restart.
   - **Restart Erda** button → `IHostApplicationLifetime.StopApplication()`; Docker
     `restart: unless-stopped` brings it back in ~seconds. Clean, no shell access needed. This
     is also how a saved prompt edit is applied.

## Prompt application (v1: restart-to-apply)

Today the agent is a **keyed singleton** built once at startup
(`builder.AddAIAgent(ErdaAgent.Name, (sp,_) => ErdaAgent.Create(sp))`) with the system prompt
baked in via `chatClient.AsAIAgent(instructions: …)`.

**v1 plan (minimal edits, no live hot-swap):** `ErdaAgent.Create` reads the **active**
`PromptVersion` from `IPromptStore` and uses its content as the instructions (falling back to
the in-code default constant if the table is empty — which also seeds it). The agent is still
built once at startup; `ErdaAgentResponder` is **unchanged**. Saving a new prompt version in the
panel writes the DB row and surfaces "restart to apply"; the **Restart Erda** button (Docker
`restart: unless-stopped`) rebuilds the agent from the new active version on the way back up.

Live hot-swap (rebuild-on-save, session reset) is an explicit **out-of-scope follow-up** — it is
the riskiest part (MAF instruction-swap behaviour) and is deferred so v1 stays small.

## Config application (v1: restart-to-apply)

**v1 plan (minimal edits, no `IOptionsMonitor` refactor):** add a read-once
`SqliteConfigurationSource`/`Provider` to `builder.Configuration`, loading `ConfigOverrides` rows
(keys already in `Section:Key` form) layered **after** appsettings/env so they win. All existing
consumers keep `IOptions<T>` exactly as today — they bind these values at startup. The panel
writes a `ConfigOverrides` row; the **Restart Erda** button applies it.

Live reload (`IOptionsMonitor.CurrentValue` per tick/call, change tokens) is an explicit
**out-of-scope follow-up** — it is the cross-cutting, edit-heavy part and is deferred for v1.

Some keys can never be hot-applied anyway (chat **model/deployment** baked into the chat client,
the **LAN bind port**), so restart-to-apply is the uniform, predictable v1 behaviour.

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
- **SqliteConfigurationProvider** — overrides win over appsettings at the startup load.
- **ActivityRecorder** — append + bounded prune; subscribers notified.
- **ErdaAgent prompt seed** — `ErdaAgent.Create` uses the active prompt version; falls back to
  the in-code default constant (and seeds it) when the table is empty.
- **Migration** — one-time import from a sample legacy note + sidecar seeds the DB correctly.
- Blazor components kept thin; logic tested via the services above (no component-render tests in
  v1).

## Out-of-scope follow-ups (noted, not built)

- **Live hot-reload of prompt + config** (apply without a restart): rebuild-on-save + session
  reset for the prompt, and `IOptionsMonitor`/change-token reload for config. Deliberately
  deferred from v1 — these are the edit-heavy, riskiest parts; v1 is uniformly restart-to-apply.
- **Tailscale exposure** of the panel for remote management (the natural answer if the LAN-only
  trade-off bites).
- Persisting full conversation history / a transcript browser in the panel.
- SQLite→Postgres if multi-client or web-scale needs ever appear (EF makes it a provider swap).
- Editing tool descriptions / per-tool toggles from the panel.
- Auth beyond the single optional password (real users/roles).
- Consolidating Seq-sourced metrics/dashboards into the Activity screen.
