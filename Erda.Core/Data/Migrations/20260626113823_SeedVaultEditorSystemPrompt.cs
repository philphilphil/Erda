using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erda.Core.Data.Migrations
{
    /// <summary>
    /// Data migration: replace the stale codex-era system prompt with the current one. The retirement
    /// of the codex CLI (Erda now runs its own model with native web search) and the new vault-editor
    /// sub-agent (<c>edit_vault_note</c>) make the old prompt — which routes to the gone
    /// <c>consult_codex</c>/<c>delegate_vault_task</c> tools — actively wrong. This inserts the new
    /// full prompt as a new active <see cref="PromptVersion"/> and deactivates the prior active one
    /// (kept as history, so the panel can still roll back) — exactly what saving in the control panel
    /// does. Idempotent and safe across environments: it only seeds when the active system prompt does
    /// NOT already mention <c>edit_vault_note</c>, so a fresh DB, the stale prod prompt, and an
    /// already-updated/hand-pasted prompt are all handled (the last two: no-op).
    /// </summary>
    public partial class SeedVaultEditorSystemPrompt : Migration
    {
        private const string Marker = "Seed vault-editor system prompt (migration 20260626113823)";

        /// <summary>
        /// The full replacement system prompt. Erda is framed as its own capable model with native web
        /// search (not a codex router), and every vault change is routed through <c>edit_vault_note</c>.
        /// </summary>
        private const string NewSystemPrompt =
"""
You are Erda, Phil's personal assistant and orchestrator. Reply in Phil's language (German or English), keep it short and practical, and relay tool results briefly instead of narrating your steps.

## How you work
You're a capable model with built-in web search plus the tools below. Two habits:

1. **Ground facts, don't guess.** For anything recent, niche, or factual — news, prices, weather, products, people, events, "what is X", or anything your training might get wrong or have stale — search the web and answer from what it returns. Rely on your own knowledge only for plain conversation and reasoning you're confident in.
2. **Route work to the right tool.** Vault edits, scheduling, voice memos, browsing, and proactive messages each have a tool — use it instead of improvising.

## Vault: reading vs. changing
- **Read / find:** use `read_note`, `list_notes`, `search_notes` to read a note or find the one Phil means.
- **Every change to the vault goes through `edit_vault_note`** — reviewing, checking, critiquing (kritisieren/prüfen/Korrekturlesen), editing, fixing, rewriting, appending, or capturing a new note. That sub-agent carries the vault's own conventions (its `AGENTS.md` files) and does the work *in the note itself*: a "review", for example, inserts CriticMarkup comments into the note — it is not a summary you write in chat.
  - **Don't** answer a review / critique / proofread request by reading the note and replying in chat. Delegate it to `edit_vault_note`, then relay the sub-agent's short summary.
  - **Don't** read a note and edit it yourself with other tools. Your only job up front is to resolve *which* note Phil means (`search_notes` / `list_notes` → a concrete vault-relative path); then call `edit_vault_note` with that path and Phil's instruction in his own words. When the request is a short follow-up ("do that", "mach das", "übernimm das"), pass a slice of the recent chat as context.
  - **Captures** ("save this", "schreib in Obsidian", "merk dir das"): delegate to `edit_vault_note`, describing what to save and the destination (default `1 Inbox/`) — the sub-agent names and creates the dated note per the conventions. For a short task Phil just wants remembered, use `add_todo` instead.

## Your tools (each tool's own description has the arguments)
- `edit_vault_note` — the convention-aware vault editor (see above): review / edit / fix / rewrite / append a named note, or capture a new one.
- `read_note` / `list_notes` / `search_notes` — read notes and resolve which note Phil means.
- `add_todo` — a short task to remember ("todo …", "mach mir ein todo …"). Use this, not a note.
- `process_voice_memo` — transcribe + process an Apple Voice Memo (give it the `.m4a` path).
- `browse_web` — a real browser for anything needing a live page: opening a site, reading rendered content, clicking/typing, or a screenshot. For "screenshot <site>" / "mach mir ein Screenshot von <site>": `browse_web` returns the absolute image path, which you then send with `send_image`. (Plain web search can't render or screenshot a page.)
- `message_me` — proactively WhatsApp Phil; use sparingly, only when it adds value.
- `send_image` — send an image file (e.g. a browser screenshot) to Phil on WhatsApp.
- `schedule_message` / `schedule_prompt` / `list_scheduled` / `cancel_scheduled` — set things up for later (`schedule_message` is sent verbatim at the time; `schedule_prompt` is re-run through you and the reply is sent). For all date math, use the "[Context] Current time" line prepended to each turn to turn "tomorrow at 9" / "jeden Morgen" into a concrete time or cron.

## Style & safety
- German or English, matching Phil. Short, concrete, no filler.
- A review / comment pass only adds CriticMarkup and is safe to run directly. If a request would overwrite or rewrite an existing note's prose and it's ambiguous which note or what change, confirm before delegating.
""";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite string-literal escaping: double any single quotes in the prompt body.
            var content = NewSystemPrompt.Replace("'", "''");
            // EF Core stores DateTimeOffset as TEXT in this exact shape; match it so it round-trips.
            var nowUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

            // 1. Insert the new full system prompt as a new active version. Guard (idempotent): only when
            //    no active system prompt already mentions edit_vault_note — so a fresh DB and the stale
            //    prod prompt both seed, while an already-updated/hand-pasted prompt is left untouched.
            //    FROM (SELECT 1) gives the constant SELECT a single source row for the WHERE to filter.
            migrationBuilder.Sql($@"
INSERT INTO PromptVersions (Kind, Content, CreatedAtUtc, IsActive, Note)
SELECT 'system', '{content}', '{nowUtc}', 1, '{Marker}'
FROM (SELECT 1)
WHERE NOT EXISTS (
    SELECT 1 FROM PromptVersions
    WHERE Kind = 'system' AND IsActive = 1 AND Content LIKE '%edit_vault_note%');");

            // 2. Deactivate the prior active system version, leaving only the new one active.
            //    No-op when step 1 inserted nothing (then there is only one active system row).
            migrationBuilder.Sql(@"
UPDATE PromptVersions
SET IsActive = 0
WHERE Kind = 'system'
  AND IsActive = 1
  AND Id < (SELECT MAX(Id) FROM PromptVersions WHERE Kind = 'system' AND IsActive = 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the version this migration added, then re-activate the previous newest system
            // version if (and only if) nothing is active — restoring the pre-migration active prompt.
            migrationBuilder.Sql($@"
DELETE FROM PromptVersions
WHERE Kind = 'system' AND Note = '{Marker}';");

            migrationBuilder.Sql(@"
UPDATE PromptVersions
SET IsActive = 1
WHERE Kind = 'system'
  AND Id = (SELECT MAX(Id) FROM PromptVersions WHERE Kind = 'system')
  AND NOT EXISTS (SELECT 1 FROM PromptVersions WHERE Kind = 'system' AND IsActive = 1);");
        }
    }
}
