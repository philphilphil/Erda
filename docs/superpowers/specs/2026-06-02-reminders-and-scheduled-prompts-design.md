# Reminders & Scheduled Prompts — Design

- **Date:** 2026-06-02
- **Status:** Approved (design); pending implementation plan
- **Author:** Phil + Erda (Claude)

## Summary

Let Phil tell Erda to remind him of things, or to run a prompt on a schedule. Two
behaviours:

1. **Reminder (verbatim)** — at the scheduled time, Erda sends Phil's stored text over
   WhatsApp. No model call at fire time.
2. **Scheduled prompt** — at the scheduled time, Erda runs the stored prompt through the
   `erda` agent (in a fresh, throwaway session) and sends the reply over WhatsApp. e.g.
   `0 6 * * *` → "What's the weather in Munich today? Brief." The agent uses its normal
   tools (`consult_codex` for web-grounded facts, vault tools, etc.).

Both one-shot (`2026-06-15 09:00`) and recurring (cron `0 6 * * *`) timing is supported.

The **source of truth is a Markdown note in the Obsidian vault** (`Atlas/AI/Erda/Reminders.md`),
so Phil can read, add, edit, pause, or delete reminders directly in Obsidian (including from
his phone), and the agent's `schedule_*` tools just append rows to the same note. A small JSON
sidecar holds machine-only runtime state (last-fired bookkeeping) so the scheduler never has to
rewrite the note every tick.

This mirrors the existing `ErrorWatchScheduler` stack almost exactly: a `BackgroundService` on a
`PeriodicTimer` that polls, optionally runs the model, and pushes to WhatsApp via
`IWhatsAppSender`.

## Goals

- Verbatim reminders and agent-run scheduled prompts, one-shot and recurring.
- Vault note as the editable source of truth; round-trips with the `schedule_*` tools.
- Robust across restarts and downtime: no double-fires, no backfill storms.
- Cron via Cronos; times interpreted in Europe/Berlin (config-overridable), DST-correct.
- Fits the existing architecture and test style (`ErrorWatchScheduler` / `Fakes.cs`).

## Non-goals (v1)

- No snooze / reschedule-on-reply, no per-reminder timezone override, no multi-user.
- No natural-language date parsing in the store/scheduler — the **model** supplies a concrete
  `when` (ISO datetime or cron). To do that for relative phrases ("tomorrow 9am"), the agent is
  given the current local time per turn (see *Supporting change: current-time context*).
- No web UI; Obsidian + the tools are the interface.

## Architecture

Four new components (plus two small supporting changes), under existing namespaces:

| Component | Namespace | Mirrors |
|---|---|---|
| `Reminder`, `ReminderKind`, `WhenSpec` (records/enums) | `Erda.Scheduling` | `ErrorAlert` / `SeqError` |
| `ReminderStore` (vault-backed parse + write) | `Erda.Scheduling` | (new; uses `VaultService`) |
| `ReminderState` + `ReminderStateStore` (JSON sidecar) | `Erda.Scheduling` | `ErrorWatchState` / `ErrorWatchStateStore` |
| `ReminderScheduler : BackgroundService` | `Erda.Scheduling` | `ErrorWatchScheduler` |
| `ReminderTools` (`schedule_*` agent tools) | `Erda.Tools` | `NotifyTools` / `ObsidianTools` |
| `IAgentResponder.RunOnceAsync` (fresh-session run) | `Erda.Agents` | extends `ErdaAgentResponder` |

### Data flow

```
Phil (WhatsApp / Obsidian)
   │  "remind me tomorrow 9am to call mom"
   ▼
erda agent ── schedule_message ──► ReminderStore.Append ──► Atlas/AI/Erda/Reminders.md
                                                                   ▲
                                                          (Phil can also hand-edit)
                                                                   │
ReminderScheduler (every 1 min)  ── ReminderStore.LoadAll ─────────┘
   │  for each due, active reminder:
   ├─ Reminder kind ──► IWhatsAppSender.SendAsync(message)
   └─ Prompt kind   ──► IAgentResponder.RunOnceAsync(prompt) ──► IWhatsAppSender.SendAsync(reply)
   │  then: advance sidecar last-fired; mark one-shots status:done in the note
   ▼
ReminderState (JSON sidecar, LocalApplicationData/erda/reminder-state.json)
```

## The vault note (source of truth)

Path from config `Reminders:NotePath`, default `Atlas/AI/Erda/Reminders.md`. One note, two
tables under two H2 sections. **The section header determines the kind** (verbatim vs prompt) —
there is no `kind` column to eyeball.

```markdown
# Erda Reminders

Managed by Erda. You can edit, add, pause (status: paused), or delete rows here.
Times are Europe/Berlin. `when` is either a date-time (2026-06-15 09:00, fires once)
or a cron expression (0 6 * * *, recurring; @daily/@weekly also work).

## Reminders
Sent to me verbatim at the scheduled time.

| id       | when             | message            | status |
|----------|------------------|--------------------|--------|
| call-mom | 2026-06-15 09:00 | Call mom 🎂        | active |
| trash    | 0 19 * * 0       | Take out the trash | active |

## Scheduled prompts
Run through Erda; the reply is sent to me.

| id      | when      | prompt                               | status |
|---------|-----------|--------------------------------------|--------|
| weather | 0 6 * * * | What's the weather in Munich? Brief. | active |
```

### Columns

- **id** — stable key (used by the sidecar and `cancel_scheduled`). Tool-created rows get a
  generated slug (kebab-case of the text, umlauts folded ae/oe/ue/ss, ≤24 chars, deduped with
  `-2`, `-3`…). Hand-added rows that leave `id` blank get a **deterministic derived id** =
  `h<hash>` over `section|when|text` (so they work without write-back; editing the row's
  `when`/`text` changes the derived id and resets its fired-state — acceptable, "edited = new").
- **when** — one of:
  - **Date-time** `yyyy-MM-dd HH:mm` (also accept `yyyy-MM-ddTHH:mm`) → **one-shot**, local
    Europe/Berlin.
  - **Cron** standard 5-field (`m h dom mon dow`) or Cronos macro (`@daily`, `@weekly`,
    `@hourly`) → **recurring**.
  - Parser tries date-time (exact formats) first; else `CronExpression.Parse(.., CronFormat.Standard)`;
    else the row is **invalid** (skipped + logged; see *Error handling*).
- **message** / **prompt** — the payload. `message` is sent verbatim; `prompt` is run through
  the agent. Pipe characters in the text are escaped (`\|`) on write and unescaped on read.
- **status** — `active` (eligible), `paused` (skipped; for manual pause in Obsidian), or `done`
  (auto-set by the scheduler after a one-shot fires). Recurring rows are never set to `done`.

### Tool ↔ section mapping

- `schedule_message` → **Reminders** section (kind = Reminder, verbatim).
- `schedule_prompt` → **Scheduled prompts** section (kind = Prompt, agent-run).

### Write semantics

`ReminderStore` does targeted read-modify-write via `VaultService`:

- **Append** (tool): read note (scaffold both sections + table headers if the file is missing —
  `VaultService.WriteNote` creates parent dirs), locate the target section's table, insert the
  new row after the last data row, write back.
- **Mark done / status change** (scheduler + `cancel_scheduled`): find the row line by `id`,
  replace only its `status` cell (or remove the row for cancel).
- All writes are serialized by a `SemaphoreSlim` inside `ReminderStore` so the scheduler's
  done-marking and a tool's append can't interleave. Cross-process races with Obsidian are
  accepted as **last-writer-wins** (single user, rare; the scheduler only writes on one-shot
  completion / cancel, not every tick).

## Sidecar runtime state

`ReminderState`, persisted as JSON exactly like `ErrorWatchState` (best-effort load/save; path
from `Reminders:StateFile`, default `LocalApplicationData/erda/reminder-state.json`):

```jsonc
{
  "LastFiredUtc":    { "<id>": "2026-06-02T04:00:00+00:00" },  // recurring cadence
  "FiredOneShotIds": [ "<id>" ]                                // send-once backstop
}
```

- `LastFiredUtc` keyed by reminder id drives recurring cadence and survives restarts.
- `FiredOneShotIds` is the **send-once guarantee** for one-shots even if the note `status:done`
  write fails (the note flag is the visible-in-Obsidian copy; the sidecar is the authority for
  "already sent"). Trimmed to the most recent ~1000; ids absent from the note are pruned on load.

The note holds **definitions**; the sidecar holds **machine state**. They are deliberately
separate so the scheduler does not have to write to a file Phil is editing on every tick.

## Scheduler tick

`ReminderScheduler : BackgroundService`, structured like `ErrorWatchScheduler`: guard clauses
(disabled / no owner number → log and don't start), `PeriodicTimer(opts.PollInterval)`, each tick
wrapped in try/catch so one failure retries next interval. A public `PollOnceAsync(...)` is
exposed for tests (as `ErrorWatchScheduler.PollOnceAsync` is).

For each `active` reminder at instant `nowUtc` (tz = `TimeZoneInfo.FindSystemTimeZoneById(opts.TimeZone)`):

### One-shot (`when` is a date-time → `dueUtc`)
1. If `id ∈ FiredOneShotIds` → ensure note `status:done`; skip.
2. If `nowUtc < dueUtc` → not due; skip.
3. If `nowUtc - dueUtc > OverdueGrace` → **stale**: mark done (note + `FiredOneShotIds`), do
   **not** fire, log. (Prevents a flood of late one-shots after long downtime.)
4. Else → **fire** (see Dispatch). On success: add to `FiredOneShotIds`, set note `status:done`.
   On failure: log + optional notify, leave `active` (retries next tick while still within grace).

### Recurring (`when` is cron)
1. `lastFired = LastFiredUtc[id]`. If absent (first sight) → set `LastFiredUtc[id] = nowUtc`,
   persist, **skip** (never backfire occurrences from before Erda saw the reminder).
2. `occ = cron.GetNextOccurrence(lastFired, tz)` (strictly after `lastFired`, returned as UTC).
3. If `occ` has a value and `occ ≤ nowUtc` → **fire**. Set `LastFiredUtc[id] = nowUtc`
   (**not** `occ`) so any further missed occurrences are skipped — **no backfill**; at most one
   fire per reminder per tick. Advance `lastFired` even on dispatch failure (don't hammer a
   broken prompt every minute; it retries at the next occurrence).
4. Else → skip.

### Dispatch
- **Reminder kind** → `IWhatsAppSender.SendAsync(ownerJid, message, ct)`.
- **Prompt kind** → `IAgentResponder.RunOnceAsync([user: prompt], ct)` → send `reply.Text`
  (fallback "(no response)" if empty), prefixed with a small marker so a scheduled push is
  distinguishable from a live reply (e.g. `⏰ <label>:`).

Due reminders within a tick are processed sequentially (the loop is sequential), so scheduled
prompts don't run concurrently with each other.

## Agent run seam (fresh session)

`ErdaAgentResponder` currently exposes only a single long-lived `AgentSession` (the live WhatsApp
conversation, serialized by `_gate`). Add:

```csharp
Task<AgentReply> RunOnceAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
```

It creates a **fresh throwaway session** (`agent.CreateSessionAsync`), runs once, and returns the
reply — it does **not** touch `_session` or `_gate`, so a scheduled prompt neither pollutes nor
blocks the live conversation. (Assumption: an `AIAgent` is safe to run on a second session
concurrently with the live one; if testing shows otherwise, add a dedicated `_scheduledGate` that
serializes scheduled runs only. The same telemetry/usage extraction as `RespondAsync` is reused.)

## Tools (`ReminderTools`, registered on the agent)

Built like `NotifyTools`/`ObsidianTools` (`AsTools()` → `AIFunctionFactory.Create`), added to the
tool list in `ErdaAgent.Create`, with a short usage paragraph in the system prompt.

| Tool | Params | Behaviour |
|---|---|---|
| `schedule_message` | `when` (string), `message` (string), `id?` (string) | Append a row to **Reminders**. Validates `when`. Returns confirmation incl. the next fire time. |
| `schedule_prompt` | `when` (string), `prompt` (string), `id?` (string) | Append a row to **Scheduled prompts**. Same validation/confirmation. |
| `list_scheduled` | — | Return `active`/`paused` rows from both tables with their next fire time. |
| `cancel_scheduled` | `id` (string) | Remove the matching row (from either table). Returns whether it matched. |

Tool descriptions state: **times are Europe/Berlin**; `when` is either `yyyy-MM-dd HH:mm` (once)
or a cron expression (recurring), with examples (`0 6 * * *` = daily 06:00; `0 19 * * 0` = Sundays
19:00). On invalid `when`, the tool returns a clear error rather than writing a bad row. Validation
reuses the same parser as the store.

## System prompt addition (ErdaAgent)

A short block teaching the agent: use `schedule_message` for "remind me to X at/every …" (sent
verbatim) and `schedule_prompt` for "every morning tell me the weather / run X on a schedule";
convert relative times to a concrete `when` using the current time provided in context; times are
Europe/Berlin; use `list_scheduled`/`cancel_scheduled` to review or remove.

## Supporting change: current-time context

For the agent to turn "tomorrow at 9" into `2026-06-03 09:00`, it must know "now". Inject a single
context line — current Europe/Berlin date-time — into each agent run:

- In `WhatsAppChannelService.BuildMessagesAsync`, prepend a `ChatRole.System` (or leading user
  context) message: `[Context] Now: 2026-06-02 14:30 (Europe/Berlin), Tuesday`.
- `RunOnceAsync` callers (the scheduler) likewise include it.

A tiny injected-time helper (so it's testable / fakeable) reads the clock once per turn. This is
the minimal change that makes relative one-shots correct; without it the model guesses the date.

## Configuration

New `Reminders` section in `appsettings.json` (`Erda` config family), bound to a
`ReminderOptions` record (cf. `ErrorWatchOptions`):

| Key | Type | Default | Purpose |
|---|---|---|---|
| `Enabled` | bool | `true` | Master switch (off → scheduler logs and returns). |
| `NotePath` | string | `Atlas/AI/Erda/Reminders.md` | Vault-relative path to the note. |
| `TimeZone` | string | `Europe/Berlin` | IANA id; interpretation of `when`. |
| `PollInterval` | TimeSpan | `00:01:00` | Tick cadence. |
| `OverdueGrace` | TimeSpan | `24:00:00` | Late-fire window for one-shots. |
| `StateFile` | string? | (LocalAppData/erda/reminder-state.json) | Sidecar path override. |
| `NotifyOnError` | bool | `true` | WhatsApp ping on dispatch/parse failure. |

New dependency: **`Cronos`** NuGet package (cron parsing + tz-aware next-occurrence). DI wiring in
`Program.cs`: `Configure<ReminderOptions>`, `AddSingleton<ReminderStore>`,
`AddSingleton<ReminderTools>`, `AddHostedService<ReminderScheduler>`, and add `ReminderTools` to
the agent's tool list in `ErdaAgent.Create`.

## Error handling

- **Malformed row** (bad `when`, wrong column count) → skipped + logged. To avoid per-minute spam,
  a WhatsApp notice for a given malformed row is sent at most once per process lifetime (tracked
  in-memory by row content); ongoing logging stays at Debug.
- **Dispatch failure** (sender down, agent/Codex throws) → logged; if `NotifyOnError`, a one-line
  `⚠️ scheduled "<id>" failed: <reason>`. One-shot stays `active` (retries within grace); recurring
  advances cadence (won't retry until next occurrence).
- **Missing note** → treated as empty (no reminders); scaffolded on first tool write.
- **Missing owner number / disabled / unparseable tz** → guard clauses log and the scheduler does
  not start, like `ErrorWatchScheduler`.

## Edge cases

- **First-run / newly-added recurring** → seeded `lastFired = now`, so past occurrences never fire.
- **Restart mid-day** → recurring resumes from sidecar `lastFired` (no missed-occurrence storm);
  one-shots fire if now within `[due, due+grace]`, else marked done unfired.
- **Two occurrences in one minute** (e.g. `* * * * *`) → one fire per tick (minute granularity).
- **Duplicate id** on tool create → generator dedupes (`-2`…); a hand-written duplicate id is
  logged and the later row skipped.
- **DST transitions** → handled by Cronos with the IANA zone (a `0 2 * * *` on a spring-forward
  night behaves per Cronos's documented rules).
- **Note write race with Obsidian** → last-writer-wins; scheduler writes are infrequent
  (completion/cancel only).

## Testing plan (xUnit, mirroring `ErrorWatchSchedulerTests` + `Fakes.cs`)

- **`WhenSpec` parsing** — date-time vs cron vs garbage; macros; both date-time formats.
- **Next-occurrence / due logic** — recurring fires once at the occurrence; no backfill after a
  simulated gap; one-shot fires within grace, marked done unfired beyond grace; DST sanity.
- **Table parsing** — tolerant: extra whitespace, escaped pipes, `paused`/`done` skipped, missing
  `id` derives a stable id, malformed row skipped without aborting the batch.
- **`ReminderStore` round-trip** — append under correct section, scaffold when missing, mark
  status by id, remove by id; pipe escaping.
- **`ReminderState`** — load/save/trim; send-once via `FiredOneShotIds` even if note write fails.
- **`PollOnceAsync` dispatch** — with fake `IWhatsAppSender` and fake agent runner: Reminder kind
  → verbatim send; Prompt kind → runner invoked then reply sent; disabled/paused skipped; failure
  paths (one-shot stays active, recurring advances).
- **Time injection** — `BuildMessagesAsync` prepends the current-time context line.

Inject a fake clock (the same time-helper used for context injection) so all time-based tests are
deterministic — no wall-clock dependence.

## Out-of-scope follow-ups (noted, not built)

Snooze/ack on reply, per-reminder timezone, natural-language `when` parsing server-side, a
"fired log" section in the note for history.
