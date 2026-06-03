# Scheduled-Prompt Pre-Run Context Script (F2) — Design

- **Date:** 2026-06-03
- **Status:** Approved (design); pending implementation plan
- **Author:** Phil + Erda (Claude)
- **Related:** [`2026-06-03-scheduled-prompt-enhancements-design.md`](2026-06-03-scheduled-prompt-enhancements-design.md) (F1/F3/F4). This is the deferred **F2** from that batch.

## Summary

Let a **scheduled prompt** run a small shell command *before* the prompt fires, and inject that
command's output into the prompt. The point is to gather data that's cheaper/easier to fetch with a
script than to have the model fetch it — e.g. `curl` a weather API to JSON, and let the model just
read and interpret it.

Concretely, for a `Kind == Prompt` row with a non-empty pre-run script:

1. Run the script in `/bin/sh -c`, capture stdout.
2. Splice stdout into the resolved prompt text (placeholder if present, otherwise prepended under a
   labelled header).
3. Hand the composed prompt to whichever route the prompt uses (the `erda` agent today; Codex-direct
   once F1 lands).

The mechanism is an **arbitrary shell command** (chosen during brainstorming) — maximum flexibility
for a single-user, LAN-only personal tool whose schedules Phil authors himself.

## Goals

- A scheduled prompt can carry an optional shell command whose stdout is injected before the model runs.
- Injection placement is controllable (a `{{context}}` token) with a sensible default (prepend).
- Execution is bounded (timeout, output cap) and fails safe (skip + notify, never a silent wrong answer).
- The attack surface is limited: only Phil (via the panel) can set a script — **not** the agent's
  `schedule_prompt` tool.
- Reuses the existing subprocess pattern (`CodexRunner`) and interface-seam test style (`ICodexRunner`).

## Non-goals (v1)

- A "run the prompt anyway if the script fails" per-prompt mode (always skip + notify in v1).
- Multiple scripts per prompt, script chaining, or templating beyond the single `{{context}}` token.
- Scripts on verbatim **reminders** (`Kind == Reminder`) — only scheduled prompts get a script.
- A non-shell mechanism (named providers / HTTP-only). Those were the rejected alternatives in the
  F2 brainstorming question.
- Sandboxing/jailing the script beyond a timeout and a temp working directory.

## Security posture

Running an arbitrary shell command stored in a DB row is code execution by definition. This is
acceptable here because:

- **Single-user, LAN-only, plain-HTTP panel.** The same trust boundary already lets Phil edit the
  agent's system prompt and schedule agent runs.
- **Phil authors the scripts.** The panel is the *only* writer of the script field. The agent's
  `schedule_prompt` tool does **not** accept or set a script, so a prompt-injection of the agent
  can't plant a script.
- **Master switch.** `Reminders:PreScriptEnabled` (default `true`) disables all pre-script execution
  in one place; when off, a row with a script is treated as having none and logs a warning.
- **Env inheritance is intentional.** The child inherits the process environment so a script can use
  already-configured secrets (e.g. an API key). This is a deliberate utility/locality trade-off; the
  panel is the trust boundary. (Note: unlike `CodexRunner`, we do **not** strip `OPENAI_API_KEY`
  here — that stripping is specific to keeping Codex on subscription billing and is irrelevant to a
  user-authored context script.)

---

## Data model

Add an optional script string, meaningful only when `Kind == Prompt`:

- `Erda.Core/Data/Entities/ReminderRow.cs` — new `public string? PreScript { get; set; }` column
  (nullable; `null`/empty = no script = today's behaviour).
- `Erda.Core/Scheduling/Reminders/Reminder.cs` — new `string? PreScript` field on the record.
- EF migration `AddPreScript`:
  `dotnet ef migrations add AddPreScript --project Erda.Core --startup-project Erda.Server`.
- `ReminderStore.LoadAll` reads the column.
- `ReminderStore.Append` and `ReminderStore.Update` (the latter from the F1/F3/F4 spec) gain an
  optional `string? preScript = null` parameter. **`Append`'s new parameter is not surfaced to the
  agent tool** — `ReminderTools.SchedulePrompt` keeps calling `Append` without it, so agent-scheduled
  prompts never carry a script.

> If F1/F3/F4 has not landed yet, `ReminderStore.Update` does not exist; this spec then adds it per
> the F1/F3/F4 design. The two specs are independent at the data layer (different columns) and can be
> implemented in either order; the migration name simply differs.

---

## Execution: `PreScriptRunner`

New component mirroring `CodexRunner`'s subprocess handling, behind an interface for testability:

```csharp
// Erda.Core/Services/IPreScriptRunner.cs
public interface IPreScriptRunner
{
    /// <summary>Run a shell command and return its stdout (trimmed, capped). Throws on
    /// non-zero exit, launch failure, or timeout.</summary>
    Task<string> RunAsync(string script, CancellationToken cancellationToken = default);
}
```

`PreScriptRunner` behaviour:

- **Launch:** `ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { "-c", script } }`,
  `RedirectStandardOutput`/`Error = true`, `UseShellExecute = false`. (`/bin/sh` exists on the
  Linux prod target and the macOS dev box.)
- **Working directory:** a fresh `Directory.CreateTempSubdirectory("erda-prescript-")`, deleted in a
  `finally` (same pattern as `CodexRunner`).
- **stdin:** closed immediately to send EOF (same reasoning as `CodexRunner` — avoid inheriting a
  never-closing stdin and blocking).
- **Timeout:** `opts.PreScriptTimeout` via a linked `CancellationTokenSource`; on timeout
  `proc.Kill(entireProcessTree: true)` and throw `TimeoutException` (lifted from `CodexRunner`).
- **Output cap:** trim stdout; if longer than `opts.PreScriptMaxOutputChars`, truncate and append
  `"\n…[context truncated]"`.
- **Failure:** non-zero exit throws `InvalidOperationException` including a short stderr tail; launch
  failure throws with an actionable message.
- **Logging:** model the `CodexRunner` log line — command length, elapsed ms, output chars, exit
  code. Log the script text only when message-content capture is on
  (`OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`), matching `CodexRunner`.

DI (in `Erda.Core/ServiceCollectionExtensions.cs`):

```csharp
services.AddSingleton<PreScriptRunner>();
services.AddSingleton<IPreScriptRunner>(sp => sp.GetRequiredService<PreScriptRunner>());
```

---

## Injection into the prompt

A helper composes the final prompt from the resolved prompt text and the script output:

- If the prompt text contains the literal token **`{{context}}`**, replace (all occurrences of) it
  with the script output.
- Otherwise, **prepend** a labelled block:

  ```
  [Context gathered before this prompt]
  <script stdout>

  <prompt text>
  ```

This keeps the simple case zero-config (just write a script; its output leads the prompt) while
letting a power user place the data mid-prompt with `{{context}}`.

**Rejected alternative:** pure prepend with no token. Slightly simpler, but you can't put the data
after instructions; the token costs almost nothing and is opt-in.

---

## Scheduler wiring

In `ReminderScheduler.DispatchAsync`, the `Kind == Prompt` branch, **after** `ResolvePromptText`
and **before** the agent/Codex fork:

```csharp
string promptText;
try { promptText = ResolvePromptText(r.Text); }
catch (Exception ex) { /* existing: log + return false */ }

if (preScriptEnabled && !string.IsNullOrWhiteSpace(r.PreScript))
{
    string context;
    try
    {
        context = await scriptRunner.RunAsync(r.PreScript!, ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Reminder {Id}: pre-run script failed.", r.Id);
        return false; // → NotifyFailureAsync sends "⚠️ Scheduled … failed to run"
    }
    promptText = InjectContext(promptText, context);
}

// then: agent route (responder.RunOnceAsync) — or Codex-direct once F1 lands — using promptText
```

- `preScriptEnabled` comes from `Reminders:PreScriptEnabled`. When `false`, a row's script is ignored
  (logged once per process, like the malformed-row notice, to avoid log spam).
- `IPreScriptRunner` is injected into `ReminderScheduler` (new ctor dependency). The injection step
  sits before the agent-vs-Codex decision, so it composes with F1 automatically.
- **Fail-safe:** any script failure aborts the dispatch and notifies; the model never runs on
  missing context and silently produces a misleading answer.

**Rejected alternative:** run the prompt without the context when the script fails. Risky — the model
can't tell the context is missing. Deferred to a possible future per-prompt opt-in (non-goal here).

---

## Config

New keys under the `Reminders` options (`Erda.Core/Configuration/ReminderOptions.cs`):

| Key | Type | Default | Purpose |
|---|---|---|---|
| `PreScriptEnabled` | bool | `true` | Master switch for all pre-run scripts. |
| `PreScriptTimeout` | TimeSpan | `00:00:30` | Kill a script that runs longer. |
| `PreScriptMaxOutputChars` | int | `8000` | Cap injected stdout to protect the token budget. |

---

## API / DTO

- `ReminderDto` gains `string? PreScript`.
- `CreateReminderRequest` and the F1/F3/F4 `UpdateReminderRequest` gain `string? PreScript`.
- `GET /reminders` `Map` includes it. `POST` and `PUT` persist it (only meaningful for `Prompt`
  rows; ignored/blanked for `Reminder` rows server-side).

---

## UI

The script field rides along in the **same create form and edit modal** that the F1/F3/F4 spec adds
for scheduled prompts (this is the only cross-spec dependency):

- A **"Pre-run script"** `<textarea class="mono">` (optional), shown only when type is `Prompt`,
  with a hint: *"Runs in `/bin/sh` before the prompt; stdout is injected. Use `{{context}}` to place
  it in the prompt, otherwise it's prepended."*
- A small **"script"** badge on scheduled-prompt rows that have a non-empty `PreScript`, alongside
  the F1 "Codex" badge.
- New ref `newPreScript` (create form) / modal field; sent in the create/update bodies; reset on
  close.

> Backend (data model, runner, scheduler, API) is independent of the F1/F3/F4 UI and can land first.
> The UI additions above require the F1/F3/F4 create-form + edit-modal to exist.

---

## Testing

**xUnit (`Erda.Tests`):**

- `PreScriptRunner` (real subprocess; deterministic and safe):
  - `RunAsync("echo hello")` → `"hello"`.
  - `RunAsync("printf 'a%.0s' {1..100}")` style long output → truncated at the cap with the marker.
    (Or simpler: set a tiny `PreScriptMaxOutputChars` in the test and assert truncation.)
  - `RunAsync("exit 3")` → throws `InvalidOperationException`.
  - `RunAsync("sleep 5")` with a 200 ms timeout → throws `TimeoutException` and the process is gone.
- `ReminderStore`: `PreScript` round-trips through `Append`/`Update`/`LoadAll`; a row with no script
  loads `null`.
- `ReminderScheduler` (with a **fake `IPreScriptRunner`**):
  - A due `Prompt` row with a `PreScript` injects the fake's output into the message handed to the
    fake `IAgentResponder` (assert the user message contains the context).
  - `{{context}}` token substitution vs. prepend fallback both produce the right composed text.
  - A failing fake script → `NotifyFailureAsync` notice sent, `IAgentResponder` **not** called.
  - `PreScriptEnabled = false` → script ignored, prompt runs verbatim through the agent.
- Existing reminder-scheduler tests get the new `IPreScriptRunner` ctor dependency (a no-op fake).

**Web:**

- `vue-tsc` type-check; manual verification via `make web`: add a prompt with
  `echo '{"tempC":21}'` as the script and `Summarise: {{context}}` as the prompt, confirm the badge
  and that the fired result reflects the injected JSON.

## Files touched (anticipated)

- `Erda.Core/Data/Entities/ReminderRow.cs` — `PreScript` column.
- `Erda.Core/Data/Migrations/*` — `AddPreScript`.
- `Erda.Core/Scheduling/Reminders/Reminder.cs` — record field.
- `Erda.Core/Scheduling/Reminders/ReminderStore.cs` — load + `Append`/`Update` param.
- `Erda.Core/Scheduling/Reminders/ReminderScheduler.cs` — inject `IPreScriptRunner`; pre-script step + `InjectContext`.
- `Erda.Core/Services/PreScriptRunner.cs` + `IPreScriptRunner.cs` — new.
- `Erda.Core/Configuration/ReminderOptions.cs` — three new keys.
- `Erda.Core/ServiceCollectionExtensions.cs` — register `IPreScriptRunner`.
- `Erda.Server/Api/Reminders/ReminderEndpoints.cs` — persist `PreScript` on POST/PUT.
- `Erda.Server/Api/Reminders/ReminderDtos.cs` — DTO/request fields.
- `web/src/api/{client,types}.ts` — `preScript` on DTO + create/update bodies.
- `web/src/views/RemindersView.vue` — script textarea + "script" badge (on the F1/F3/F4 form + modal).
- `Erda.Tests/*` — runner, store, and scheduler tests; fake `IPreScriptRunner`.
