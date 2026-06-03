# Scheduled-Prompt Enhancements — Design

- **Date:** 2026-06-03
- **Status:** Approved (design); pending implementation plan
- **Author:** Phil + Erda (Claude)

## Summary

Three improvements to the **Schedules** screen and the reminder scheduler, all scoped to
**scheduled prompts** (`Kind == Prompt`):

1. **F1 — "Direct to Codex" toggle.** A per-prompt switch that makes the scheduler call Codex
   directly (web search on) and send the result to WhatsApp, bypassing the MAF `erda` agent.
   Good for big prompts (e.g. a daily news digest) where the agent's tool-routing adds no value.
2. **F3 — Human-readable cron everywhere.** Replace the hand-rolled `describeCron()` (which falls
   back to the raw expression for anything it doesn't recognise — e.g. `5 * * * *`) with the
   `cronstrue` library, used in both schedule tables and as a live preview in the create form and
   edit modal.
3. **F4 — Edit scheduled prompts in a modal.** A pencil action on each scheduled-prompt row opens a
   popup editor (schedule + prompt textarea with char/token counts + the Codex toggle) that updates
   the row in place, preserving its status and run-state.

**Out of scope (deferred to their own specs):** F2 — running a script/fetch before the prompt to
inject extra context (the chosen mechanism is an arbitrary shell command); editing verbatim
reminders (this spec edits scheduled prompts only).

## Goals

- A scheduled prompt can run via Codex-direct (web search on, default reasoning effort) instead of
  the agent, chosen per prompt.
- Every cron schedule shows a plain-English description (both tables, create form, edit modal).
- Scheduled prompts are editable in a modal without losing their status (Active/Paused) or
  last-fired bookkeeping.
- Fits the existing architecture and test style (`IAgentResponder`/`IWhatsAppSender`/`IClock`
  interface seams; xUnit fakes; Vue SPA component conventions).

## Non-goals (this spec)

- The pre-prompt context script (F2).
- Editing verbatim reminders.
- Per-prompt web-search or reasoning-effort knobs for Codex-direct (toggle only; web search on,
  effort = global default).
- Switching a row's `Kind` (Reminder ↔ Prompt) during edit.

---

## F1 — "Direct to Codex" toggle

### Data model

Add a boolean `DirectToCodex` (default `false`), meaningful only when `Kind == Prompt`:

- `Erda.Core/Data/Entities/ReminderRow.cs` — new `public bool DirectToCodex { get; set; }` column.
- `Erda.Core/Scheduling/Reminders/Reminder.cs` — new field on the `Reminder` record.
- EF migration `AddDirectToCodex`:
  `dotnet ef migrations add AddDirectToCodex --project Erda.Core --startup-project Erda.Server`.
- `ReminderStore.LoadAll` reads the column into the record.
- `ReminderStore.Append` gains an **optional** `bool directToCodex = false` parameter, so the
  agent's `schedule_prompt` tool (and any other caller) keeps compiling and defaults to the agent
  route.

**Rejected alternative:** a third `ReminderKind.CodexPrompt` enum value. It would fragment the
`Where(r => r.Kind == ReminderKind.Prompt)` grouping that drives the two panel tables and the
agent tool. A bool keeps `Kind` binary (verbatim vs. model-run) and the row stays naturally in the
"Scheduled prompts" table.

### Dispatch

In `ReminderScheduler.DispatchAsync`, the `Kind == Prompt` branch forks on `DirectToCodex`:

- **`true`** — resolve the prompt text as today (`ResolvePromptText`, so `@vault/note.md` still
  works), prepend the current-time context string, then:
  ```csharp
  var text = await codex.RunPromptAsync(fullPrompt, enableWebSearch: true, ct);
  var sent = await sender.SendAsync(ownerJid, $"⏰ {text}", ct);
  if (sent) recorder.Record("scheduled_fire", $"Prompt '{r.Id}' ran (codex)", new { r.Id, r.When });
  ```
  Wrapped in try/catch; on any exception (timeout, Codex auth expiry, non-zero exit) log a warning
  and return `false`, so the existing `NotifyFailureAsync` path sends "⚠️ Scheduled … failed to run".
- **`false`** — today's `responder.RunOnceAsync(...)` agent path, unchanged.

`CurrentTimeContext` gains a `public string Text()` returning the same string that `Message()`
wraps in a `ChatMessage`; `Message()` is refactored to call `Text()`. The Codex-direct path uses
`Text()` so Codex knows the date (important for a daily-news prompt).

### Test seam

Introduce `ICodexRunner` in `Erda.Core/Services/` exposing the one method the scheduler needs:

```csharp
public interface ICodexRunner
{
    Task<string> RunPromptAsync(string prompt, bool enableWebSearch = false,
        CancellationToken cancellationToken = default,
        string? logLabel = null, string? reasoningEffort = null);
}
```

`CodexRunner` implements it (signature already matches). `ReminderScheduler` depends on
`ICodexRunner`. DI registers both:

```csharp
services.AddSingleton<CodexRunner>();
services.AddSingleton<ICodexRunner>(sp => sp.GetRequiredService<CodexRunner>());
```

Other consumers (`VoiceMemoWorkflow`, reasoning tools) keep using the concrete `CodexRunner`; only
the scheduler takes the interface. This mirrors the codebase's existing interface seams and lets
the Codex-direct branch be unit-tested with a fake instead of shelling out to the `codex` CLI.

### API / DTO

- `ReminderDto` gains `bool DirectToCodex`.
- `CreateReminderRequest` gains `bool? DirectToCodex` (read as `?? false`; only applied when the
  kind parses to `Prompt`).
- `GET /reminders` `Map` includes the flag.

### UI

- New-schedule form (`RemindersView.vue`): a "Run directly via Codex (skip the agent)" checkbox,
  shown only when `newKind === 'Prompt'`. New ref `newDirectToCodex`, reset on close, sent in the
  `createReminder` body.
- Scheduled-prompts table: a small "Codex" badge on rows where `directToCodex` is true.

---

## F3 — Human-readable cron everywhere

Replace `describeCron()` and its hand-rolled pattern matching with **`cronstrue`** (add to
`web/package.json`). A small wrapper:

```ts
import cronstrue from 'cronstrue'
function describeCron(expr: string): string {
  try {
    return cronstrue.toString(expr, { use24HourTimeFormat: true, verbose: false })
  } catch {
    return expr // unparseable → show the raw expression
  }
}
```

- Keep `isCron(when)` (5-field check) to choose cron vs. one-time, exactly as today:
  `isCron(r.when) ? describeCron(r.when) : 'one-time'`.
- Used in **both** the Reminders and Scheduled-prompts tables.
- Added as a **live preview** under the cron input in the create form (replacing the static
  "min hour dom mon dow" hint when a valid cron is typed) and in the edit modal.
- Exact `cronstrue.toString` option names confirmed via Context7 at build time (per the project's
  library-docs rule).

**Rejected alternative:** keep extending the hand-rolled matcher. Enumerating every valid cron by
hand is open-ended; a library is the correct call.

---

## F4 — Edit scheduled prompts in a modal

### Backend

- `ReminderStore.Update(string id, string when, string text, bool directToCodex)` updates only the
  definition columns (`When`, `Text`, `DirectToCodex`) and **leaves `Status`, `LastFiredUtc`, and
  `Fired` untouched** — unlike `Append`, which forces `Status = Active`. `Kind` is not changed.
  Returns `false` if no row matches.
- New endpoint `PUT /reminders/{id}` with body `{ when, text, directToCodex }`:
  - 400 if `text` is blank or `WhenSpec.TryParse(when)` fails.
  - 404 if the id is unknown.
  - On success, returns the updated `ReminderDto` with `NextFire` recomputed.
- The id is **stable** (no re-slug on edit), so the scheduler's `LastFiredUtc[id]` keeps tracking
  the same row. Changing the cron simply changes the next computed occurrence — no state reset.

### Frontend

- New reusable `web/src/components/Modal.vue`: a fixed overlay + centered card, closes on backdrop
  click and `Esc`, with a `title` prop and default slot. (No modal primitive exists today.)
- Edit modal (a section in `RemindersView.vue`, or a small `EditScheduledPromptModal.vue`), opened
  by a new pencil button in each **scheduled-prompt** row's actions. Contents:
  - Schedule controls mirroring the create form: a datetime/cron switch, preselected by detecting
    whether the current `when` is a 5-field cron or a datetime; `VueDatePicker` for datetime, a
    mono text input for cron, with the cronstrue live preview.
  - A mono prompt `<textarea>` with **char and ~token counts** using the same heuristic as the
    Prompt editor (`tokens = Math.max(1, Math.round(len / 4))`), inlined.
  - The "Direct to Codex" checkbox (bound to the row's `directToCodex`).
  - Save → `PUT /reminders/{id}` then reload; Cancel/`Esc`/backdrop closes without saving.
- API client (`web/src/api/client.ts`): `updateReminder(id, body)` → `put<ReminderDto>(...)`.
  Add a `put` helper if one isn't already present (CSRF header handled like the existing
  `post`/`del`). Types: extend `ReminderDto` with `directToCodex`, add `UpdateReminderBody`, and add
  `directToCodex?` to `CreateReminderBody`.

**Rejected alternative:** a one-off inline overlay in `RemindersView`. A tiny reusable `Modal.vue`
is barely more code and keeps the view focused; it's also reusable for future panel dialogs.

---

## Testing

**xUnit (`Erda.Tests`):**

- `ReminderStore.Update`: updates `When`/`Text`/`DirectToCodex`; **preserves** `Status` and
  run-state columns; returns `false` for a missing id.
- `PUT /reminders/{id}` validation: bad cron → 400, blank text → 400, unknown id → 404, happy path
  returns updated DTO.
- `ReminderScheduler` dispatch: a `Prompt` row with `DirectToCodex = true` calls the **fake
  `ICodexRunner`** (asserts web search requested) and sends its result — the fake `IAgentResponder`
  is *not* invoked; with `DirectToCodex = false` the agent path runs and Codex is not called.
- Existing reminder-scheduler tests get the new `ICodexRunner` ctor dependency (a no-op fake when
  the branch isn't exercised).

**Web:**

- `vue-tsc` type-check (`cd web && npm run build`).
- Manual verification via `make web`: add a Codex-direct prompt, confirm the badge and the cronstrue
  description; edit a prompt in the modal and confirm status is preserved.

## Files touched (anticipated)

- `Erda.Core/Data/Entities/ReminderRow.cs` — `DirectToCodex` column.
- `Erda.Core/Data/Migrations/*` — `AddDirectToCodex`.
- `Erda.Core/Scheduling/Reminders/Reminder.cs` — record field.
- `Erda.Core/Scheduling/Reminders/ReminderStore.cs` — load/append/`Update`.
- `Erda.Core/Scheduling/Reminders/ReminderScheduler.cs` — Codex-direct branch; `ICodexRunner` dep.
- `Erda.Core/Services/CodexRunner.cs` + new `ICodexRunner.cs` — interface seam.
- `Erda.Core/Services/CurrentTimeContext.cs` — `Text()`.
- `Erda.Core/ServiceCollectionExtensions.cs` — register `ICodexRunner`.
- `Erda.Server/Api/Reminders/ReminderEndpoints.cs` — `PUT`; create reads `DirectToCodex`.
- `Erda.Server/Api/Reminders/ReminderDtos.cs` — DTO/request fields.
- `web/package.json` — add `cronstrue`.
- `web/src/api/{client,types}.ts` — `updateReminder`, `put`, DTO/body fields.
- `web/src/components/Modal.vue` — new.
- `web/src/views/RemindersView.vue` — cronstrue, Codex checkbox + badge, edit modal/pencil action.
- `Erda.Tests/*` — store, endpoint, and scheduler tests; fake `ICodexRunner`.
