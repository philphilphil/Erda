# Erda Control Panel — Vue SPA + JSON API Migration

- **Date:** 2026-06-02
- **Status:** Approved (design); pending implementation plan
- **Author:** Phil + Erda (Claude)
- **Supersedes presentation layer of:** `2026-06-02-erda-control-panel-design.md` (the v1 design,
  built as Blazor Server). This document changes **only the presentation layer**; v1 behavior,
  scope, data model, and the domain/service layer are unchanged.

## Summary

Replace the Blazor Server control panel with a **Vue 3 SPA** talking to a **JSON API** over the
existing services, secured by **ASP.NET Core cookie authentication**. The SPA is built by Vite and
served as static files by the same `erda` .NET app — still one container, one LAN port, one SQLite
volume. The domain layer (DB, stores, schedulers, agent, tools, config provider) is kept as-is; only
how the four screens are delivered changes.

v1 behavior is preserved exactly: reminders are live (the scheduler polls the DB every tick), and
prompt + config edits apply on the next **restart** (no live hot-reload).

## Goals

- Swap the presentation layer from Blazor Server to a Vue SPA + JSON API without touching domain
  behavior or the SQLite store.
- Keep the deployment shape: single `erda` container, the published LAN port, the `erda-data`
  SQLite volume. Add a Node build stage to the existing Dockerfile; no new container.
- Cookie auth for a single user, **off by default** (open on the LAN when no password is set),
  pragmatic CSRF protection suitable for single-user LAN over plain HTTP.
- All existing xUnit tests stay green; extracted UI logic (next-fire, slug, config allowlist) gets
  its own tests.

## Non-goals (unchanged from v1)

- No remote/public exposure, no auth provider, no multi-user/roles. LAN-only.
- No live hot-reload of prompt/config (restart-to-apply stays).
- No Postgres; SQLite only.
- No styling/theming. The SPA ships **essentially unstyled** semantic HTML; a design pass comes
  later as separate work. The existing Blazor theme (fonts, colors, animations) is **discarded**.

## What is kept vs. removed

**Kept untouched** (the domain/service layer):
- `Data/` — `ErdaDbContext`, `Entities`, EF migrations, `PromptStore` (`IPromptStore`).
- `Scheduling/` — `ReminderStore`, `ReminderStateStore`, `ReminderScheduler`, `WhenSpec`,
  `Reminder`/`ReminderKind`/`ReminderStatus`, error-watch.
- `Services/ActivityRecorder.cs` (`IActivityRecorder` + its `Recorded` event).
- `Configuration/` — all options + `SqliteConfigurationProvider`.
- Agent, tools, workflows, WhatsApp, observability, Serilog, DevUI wiring.

**Removed:**
- `Components/` (all six `.razor` files + the styled `App.razor` host + `Routes.razor`,
  `_Imports.razor`, `Layout/`).
- Blazor wiring in `Program.cs`: `AddRazorComponents().AddInteractiveServerComponents()`,
  `UseAntiforgery()`, `MapRazorComponents<App>().AddInteractiveServerRenderMode()`, the
  `_framework`-oriented `UseStaticFiles()` placement, and `MapGet("/", → Redirect("/panel"))`.

## Architecture

```
Browser (LAN)
  └─ Vue 3 SPA (Vite build, static files from wwwroot)
       │  fetch  credentials:'include'  + X-Requested-With: erda-panel
       │  EventSource  /api/activity/stream
       ▼
ASP.NET Core (same erda app, :5167)
  ├─ Cookie auth middleware (SameSite=Lax, Secure=false, HttpOnly)
  ├─ /api/*  minimal-API endpoint groups  ── thin DTO layer over ──┐
  ├─ /api/activity/stream (SSE)  ◄── IActivityRecorder.Recorded     │
  └─ MapFallbackToFile("index.html")  (SPA routing)                 │
                                                                    ▼
                       existing services: ReminderStore, IPromptStore,
                       IActivityRecorder, ConfigOverrides table,
                       IConfiguration, IHostApplicationLifetime
```

No CORS in dev: Vite proxies `/api` (and the SSE path) to `http://localhost:5167`, so the browser
sees a single same-origin, and `SameSite=Lax` cookies work. In prod the SPA and API share the
origin natively.

## Backend — JSON API

Minimal-API endpoint groups (not MVC controllers), matching the app's existing minimal-hosting
style. One file per area under `Api/`, each mapping a `RouteGroupBuilder` and taking the existing
services via DI. **Small DTO records only** — EF entities are never returned directly.

### DTOs (illustrative)

```csharp
record ReminderDto(string Id, string Kind, string When, string Text, string Status, string NextFire);
record RemindersResponse(IReadOnlyList<ReminderDto> Reminders,
                         IReadOnlyList<ReminderDto> ScheduledPrompts, int MalformedCount);
record CreateReminderRequest(string Kind, string When, string Text);
record PromptResponse(string ActiveContent, IReadOnlyList<PromptVersionDto> Versions);
record PromptVersionDto(int Id, DateTimeOffset CreatedAtUtc, bool IsActive, string? Note);
record SavePromptRequest(string Content, string? Note);
record ActivityDto(long Id, DateTimeOffset TimestampUtc, string Kind, string Summary);
record ConfigItemDto(string Key, string Label, string Hint, string? Effective, bool Overridden);
record ConfigUpdateRequest(IReadOnlyDictionary<string, string?> Values); // value null/"" => clear
record LoginRequest(string? Username, string Password);
record AuthState(bool AuthRequired, bool Authenticated);
```

### Endpoints

| Method + path | Behavior | Backed by |
|---|---|---|
| `GET /api/reminders` | both kinds split into `reminders` / `scheduledPrompts`, each row with computed `nextFire` + `malformedCount` | `ReminderStore.LoadAll()` + `ReminderView` helper |
| `POST /api/reminders` | validate `kind` + `when` (`WhenSpec.TryParse`) + non-empty `text`; generate unique slug id; 400 on bad input | `ReminderStore.Append` + `ReminderView` |
| `POST /api/reminders/{id}/pause` | set status Paused; 404 if unknown | `ReminderStore.SetStatus` |
| `POST /api/reminders/{id}/resume` | set status Active; 404 if unknown | `ReminderStore.SetStatus` |
| `DELETE /api/reminders/{id}` | remove; 404 if unknown | `ReminderStore.Remove` |
| `GET /api/prompt` | active content + version list (newest first) | `IPromptStore.ListVersions` |
| `POST /api/prompt` | validate non-empty (+ optional length ceiling); save new active version | `IPromptStore.SaveNewVersion` |
| `POST /api/prompt/versions/{id}/activate` | rollback; 404 if unknown | `IPromptStore.Activate` |
| `GET /api/activity?max=` | recent entries, newest first | `IActivityRecorder.Recent` |
| `GET /api/activity/stream` | SSE; replays nothing, streams new entries | `IActivityRecorder.Recorded` |
| `GET /api/config` | allowlisted keys: label, hint, effective value, overridden flag | `IConfiguration` + `ConfigOverrides` |
| `PUT /api/config` | upsert/clear overrides for allowlisted keys only (clear when blank or equal to effective non-override) | `ConfigPanelService` |
| `POST /api/config/restart` | `IHostApplicationLifetime.StopApplication()` | lifetime |
| `POST /api/auth/login` | validate creds; issue cookie; 401 on mismatch | cookie auth |
| `POST /api/auth/logout` | sign out | cookie auth |
| `GET /api/auth/me` | `{authRequired, authenticated}` | options + `HttpContext.User` |

### Extracted, testable logic

The Blazor pages held real logic that must not be lost. Move it into plain services so it is unit-
testable without rendering:

- **`ReminderView`** (`Api/` or `Scheduling/`): `NextFire(WhenSpec, zone)`, `Slugify(text)`,
  `UniqueId(baseSlug, existing)`, and `ResolveZone(ReminderOptions)` — lifted verbatim from
  `Reminders.razor` so behavior (and the timezone fallback to UTC) is preserved.
- **`ConfigPanelService`**: owns the **allowlist** (the nine `AllowlistItem`s from
  `ConfigEditor.razor` — key/label/hint), reads effective values + current overrides, and the
  save/clear rules (override written only when the value differs from the effective config and is
  non-blank; otherwise the override row is removed).

### Live activity (SSE)

`GET /api/activity/stream` sets `Content-Type: text/event-stream`, subscribes a handler to
`IActivityRecorder.Recorded`, and writes one `data: <json>\n\n` frame per new `ActivityEntry`. The
handler enqueues to a bounded `Channel<ActivityEntry>`; the endpoint drains it to the response and
flushes. On client disconnect (`HttpContext.RequestAborted`) it unsubscribes and completes. No
backfill — the SPA fetches `GET /api/activity` first, then opens the stream for new entries (same as
the Blazor `Recent(100)` + `Recorded` pattern). Cookie auth covers the GET automatically.

## Auth & CSRF

- **Cookie auth:** `AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => …)`.
  Cookie: `HttpOnly=true`, `SameSite=Lax`, `SecurePolicy=None` (plain-HTTP LAN), sliding expiration.
  On an unauthenticated API call return **401** (not a redirect to a login page) — the
  `OnRedirectToLogin` event is overridden to write 401 for `/api` paths so the SPA can react.
- **Config:** `Panel:Username` (optional, default `admin`) + `Panel:Password` (optional). Bound via
  a new `PanelOptions`.
- **Open-by-default:** when `Panel:Password` is blank, auth is **not required** — a fallback
  authorization policy lets anonymous through, and `GET /api/auth/me` returns
  `{authRequired:false, authenticated:true}`, so the SPA never shows the login view.
- **When a password is set:** every `/api/*` endpoint except `/api/auth/*` requires an authenticated
  user (applied as a group-level `RequireAuthorization`). The **SPA index/static files are always
  served** (so the login view can render); only the data calls 401. `GET /api/auth/me` returns
  `{authRequired:true, authenticated:<bool>}`.
- **CSRF:** `SameSite=Lax` already blocks the cookie on cross-site `POST/PUT/DELETE`. As
  belt-and-suspenders, **all mutating endpoints require a custom header** `X-Requested-With:
  erda-panel`, enforced by a small endpoint filter; missing/!= → 403. The typed fetch client always
  sends it; a cross-origin simple request cannot set it without a CORS preflight the server does not
  grant. No antiforgery-token round-trip. SSE/GET endpoints are exempt.

## Frontend — Vue 3 SPA (`web/`)

Vue 3 + Vite + TypeScript + Vue Router 4. **No Pinia** — four simple routes plus a small reactive
auth composable suffice; keeps the dependency surface minimal. **No styling** beyond a couple of
trivial layout basics; plain semantic HTML so a later design pass drops in cleanly.

```
web/
  package.json  vite.config.ts  tsconfig.json  index.html
  src/
    main.ts            # createApp + router
    router.ts          # routes + auth guard (checks /api/auth/me)
    api/client.ts      # typed fetch: credentials:'include', X-Requested-With header,
                       #   JSON helpers, throws ApiError, 401 -> redirect to /login
    api/types.ts       # DTO mirrors of the C# records
    composables/useAuth.ts   # reactive { authRequired, authenticated }, login(), logout()
    components/NavBar.vue
    views/
      RemindersView.vue   # table(s) + add form + pause/resume/delete
      PromptView.vue      # textarea + char/token count + save + version list + restore
      ActivityView.vue    # initial GET + EventSource stream + kind filter
      ConfigView.vue      # allowlist form + save/clear + restart button
      LoginView.vue       # username/password (username hidden if not needed)
```

- **Routes:** `/` (Reminders), `/prompt`, `/activity`, `/config`, `/login`. A global nav guard
  resolves auth state once; if `authRequired && !authenticated`, redirect to `/login`.
- **Activity:** on mount, `GET /api/activity?max=100`, then open `new EventSource('/api/activity/stream')`,
  prepend incoming entries (cap the in-memory list), filter by kind client-side; close on unmount.
- **Restart UX:** `POST /api/config/restart` then show "restarting… reload in a few seconds." (In
  local `dotnet run` the process exits and does not return — same caveat as the Blazor button; only
  in Docker does `restart: unless-stopped` bring it back.)

## Build, deploy, tooling

- **Dockerfile:** add a `FROM node:22-alpine AS web` stage: `COPY web/package*.json`, `npm ci`,
  `COPY web/ .`, `npm run build` → `/web/dist`. The runtime stage does
  `COPY --from=web /web/dist ./wwwroot`. The .NET build stage is unchanged (the `web/` folder is not
  needed by `dotnet publish`). Single `erda` image, same `EXPOSE 5167`.
- **`Program.cs`:** `app.UseAuthentication(); app.UseAuthorization();` `app.UseStaticFiles();`
  (serves `wwwroot`), map the `/api` groups, then `app.MapFallbackToFile("index.html")` for SPA
  routing. Keep DevUI/OpenAI endpoints dev-gated. The `/panel` redirect is removed.
  **Production:** the SPA is served from `wwwroot` and `/` resolves to `index.html` via the
  fallback. **Development:** the SPA runs on the Vite dev server (proxying `/api` to the backend),
  so the backend does not serve a built SPA; `MapFallbackToFile` is registered only in Production
  (the `wwwroot/index.html` does not exist in a dev checkout anyway), and `MapGet("/", → Redirect("/devui"))`
  is kept **Development-only** so `make dev` still lands on DevUI.
- **docker-compose.yml:** unchanged — same port mapping, `erda-data` volume, `Erda__DbPath`. (Add
  optional `Panel__Password` / `Panel__Username` env pass-through to `.env.example`, off by default.)
- **Makefile:** `make dev` unchanged (backend only). Add `make web` (`cd web && npm run dev`) and
  `make dev-web` (`concurrently -k` backend + Vite), mirroring `dev-wa`.
- **Docs:** update `CLAUDE.md` (presentation layer is now Vue SPA + JSON API; note `web/` and the
  dev proxy) and `README.md`.

## Error handling

- API validation failures → `400` with a JSON `{error}` message; the SPA surfaces it inline (same
  UX as the Blazor banners). Unknown id → `404`. Missing/!= CSRF header on a mutation → `403`.
  Unauthenticated when required → `401`.
- SSE handler failures never break a recorder call (the recorder is already best-effort); a stream
  error closes that connection and the SPA reconnects (EventSource auto-retries).
- Restart is intentional process exit; the SPA shows the transient banner.

## Testing plan

- **`ReminderView`** — next-fire for one-shot + cron (incl. UTC zone fallback); slug + unique-id
  collisions (parity with the old `.razor` logic).
- **`ConfigPanelService`** — effective vs. override resolution; save writes override only when
  different + non-blank; blank/equal clears the row; non-allowlisted keys ignored.
- **CSRF filter** — mutation without the header → 403; with it → passes.
- **Auth** — `auth/me` reports `authRequired:false` when no password; `true` + 401 on protected
  endpoints when set; login issues a cookie that authorizes subsequent calls. (Lightweight
  `WebApplicationFactory` integration test or focused unit tests on the filter/policy.)
- All existing tests (reminders, error-watch, prompt store, WhatsApp, etc.) stay green — they touch
  the untouched domain layer.
- **Frontend:** no component test harness in v1 (matches v1's "components kept thin"); correctness
  is in the API + extracted services. Manual verification exercises each screen.

## Verification (before claiming done)

1. `dotnet build` and `dotnet test` — all green.
2. `cd web && npm ci && npm run build` — clean Vite build.
3. Run backend (`make dev`) + Vite (`make web`); exercise each screen: create a reminder (see it
   listed with next-fire), save a prompt version (and restore one), watch the activity stream push a
   new entry live, edit a config value + save, log in / log out (with `Panel:Password` set), and
   confirm open access when it is unset.

## Out-of-scope follow-ups (unchanged)

- Live hot-reload of prompt + config, Tailscale exposure, transcript browser, Postgres swap, per-tool
  toggles, real multi-user auth, Seq-sourced dashboards in Activity. The visual/design pass for the
  SPA is separate, later work.
