# Vault-editor sub-agent

**Date:** 2026-06-26
**Status:** Approved, ready for implementation

## Problem

The retired `codex` CLI gave Erda a `delegate_vault_task` capability: Erda handed codex a
natural-language task, and codex's harness operated on the Obsidian vault directly — reading,
searching, and editing notes with its own shell, **automatically loading the vault's `AGENTS.md`
conventions** for the note's location.

Now that Erda runs as a frontier model over the plain OpenAI Responses API (`fb53b92`), that harness
is gone. The replacement (`IReasoner` / `ResponsesReasoner`) is a stateless one-shot oracle — prompt
→ text — with no vault loop and no convention loading. The main `erda` agent has coarse vault tools
(`list/read/search/write/append`), but no awareness of the vault's editing conventions.

What was actually lost is **not** multi-note autonomy (99% of real use is a single named note). It is:

1. **Hierarchical convention resolution.** The vault's root `AGENTS.md` says *"check for a nearer
   `AGENTS.md` in the note's folder tree … the nearer file wins on conflicts."* Editing
   `Efforts/On/<draft>.md` must stack **root + `Efforts/On/AGENTS.md`**.
2. **Precise CriticMarkup surgery.** Review mode forbids touching surrounding text
   ("only insert CriticMarkup"); threads require strict adjacency (no whitespace between `<<}{>>`
   blocks). Whole-file rewrites invite exactly the drift the conventions forbid.
3. **Convention isolation.** The conventions are ~100 lines of intricate, mode-dependent grammar.
   They must not sit in Erda's main system prompt on every conversational turn.

## Approach

A **vault-editor sub-agent exposed to Erda as a tool** (`edit_vault_note`). This is the MAF
agent-as-tool pattern already used for the voice-memo workflow, specialized for vault editing — but
because the sub-agent's instructions depend on the **target path**, it is built **fresh per call**
(exactly as `ResponsesReasoner` builds a one-shot agent per call), via a hand-written
`AIFunctionFactory.Create` tool rather than a pre-built `AsAIFunction`.

When Erda recognizes a vault-edit request it resolves the fuzzy reference to a concrete note path and
delegates. The sub-agent runs its own isolated loop on the same Responses endpoint, then returns a
brief chat summary. Erda never sees the sub-agent's intermediate tool calls — the convention grammar
and the editing grind stay out of Erda's context.

### Rejected alternatives

- **OpenAI Agents SDK (their option 2).** No official .NET SDK (Python/TS only); MAF already *is*
  Erda's agents SDK; adopting it re-introduces the polyglot subprocess that codex retirement removed.
- **Richer inline tools on the main agent (do-less baseline).** Defeated by hierarchical, per-folder
  conventions — they can't live cleanly in one global system prompt, and would tax every turn.

## Components

### 1. `VaultService.StackConventions(string notePath)` → `string` (Erda.Core)

Resolve the target note's path, then walk from the vault **root down through each ancestor folder of
the note (including the note's own folder)**, collect every `AGENTS.md`, and concatenate
**root-first, nearest-last**. Each chunk is headed by its scope (e.g. `### Conventions: Efforts/On/`),
and the result opens with a one-line precedence note ("Later sections are nearer the note and win on
conflict."). Missing `AGENTS.md` files are skipped. If none exist, return a minimal fallback string.

- Path-safety: reuse `ResolveInside`; never read outside the root. `.obsidian`/`.trash` dot-folders
  are not walked (consistent with `IsHidden`).
- Returns instructions text only; does not read the note itself.

### 2. `VaultService.ReplaceInNote(string path, string oldString, string newString)` (Erda.Core)

Anchored, surgical edit. `oldString` must occur in the note **exactly once**:

- 0 matches → throw/return a clear "anchor not found" error.
- >1 match → throw/return a clear "anchor is not unique, add more surrounding context" error.
- exactly 1 → replace it with `newString`, write back.

This forces the model to target precise locations and structurally enforces review-mode's
"never touch surrounding text" and thread-adjacency rules. Path-safe via `ResolveInside`.

### 3. Vault-editor tool + sub-agent (Erda.Agents/Tools)

A new class (e.g. `VaultEditorTool`) exposing one tool to Erda:

```
edit_vault_note(string path, string instruction, string? recentContext = null) → string
```

- **`[Description]`** carries the bilingual trigger vocabulary so Erda knows when to delegate:
  review/check/critique/kritisiere/prüfe/Korrekturlesen/edit/fix/rewrite/append a **named existing**
  note, plus capture-to-vault requests. (Erda owns resolving the fuzzy reference → concrete `path`.)
- **Per call**, the tool:
  1. Builds instructions = `StackConventions(path)`.
  2. Constructs a sub-agent on the **shared `ResponsesClient`** (same endpoint/model as Erda), via
     `AsAIAgent(ChatClientAgentOptions { Name = "vault-editor", ChatOptions = { Instructions, Tools,
     RawRepresentationFactory = high effort } }, ChatModel)` — mirroring `ErdaAgent.cs`.
  3. Runs `RunStreamingAsync($"Note: {path}\n\n{instruction}" + recentContext).ToAgentResponseAsync(ct)`
     (the proxy's non-streamed Responses returns empty, so streaming is mandatory).
  4. Returns `response.Text` (the "summarize briefly in chat" output) to Erda.
- **Sub-agent tool set:** `read_note`, `search_notes`, `edit_note` (anchored, §2), `write_note`
  (whole-file create/overwrite, for writing-mode new inbox notes), and `HostedWebSearchTool`
  (conventions demand fact-checking). **No** reminder/notify/browser tools.
- **Reasoning effort:** hardcoded **high** (conventions are intricate; mirrors codex's always-high
  vault task — not the model-lowerable knob).
- **Observability:** wrap the sub-agent with the same `UseOpenTelemetry` + `ToolCallActivity`
  middleware as `ErdaAgent`, so its runs surface in the activity feed.
- **Errors:** a non-existent path or empty sub-agent output returns a clear message string to Erda
  (so Erda can relay/re-resolve conversationally) rather than throwing.

### 4. Wiring & write-path consolidation

- Register `VaultEditorTool` in DI (`Erda.Agents/ServiceCollectionExtensions.cs`) and add its tool to
  the main agent's tool list in `ErdaAgent.cs`.
- **Consolidate writes:** remove `write_note` and `append_note` from `ObsidianTools.AsTools()` (and
  their now-unused private methods). The main `erda` agent keeps **`list_notes`, `read_note`,
  `search_notes`, `add_todo`** (read-side + the trivial, conventionless todo append). The
  vault-editor sub-agent becomes the **sole convention-aware writer**.
- Untouched: the voice-memo and recipe workflows write via their own executors
  (`ObsidianWriteExecutor`), independent of `ObsidianTools` — they keep working.

## Security

Strictly safer than the old codex subprocess: **no shell at all**, only typed vault tools confined to
the root by `ResolveInside`. The only egress is `HostedWebSearchTool`; an injected note cannot
exfiltrate file contents because there is no `curl`/shell to do it with. The vault is Phil's own
trusted content regardless.

## Testing

Deterministic pieces only (the model loop needs the live endpoint; mirror `CodexRunnerTests`'
approach of testing invocation invariants):

- **`StackConventions`:** root-first/nearest-last order; scope headers present; missing files
  skipped; a deeply nested note collects all ancestor `AGENTS.md`; path-safety (a path escaping the
  root throws); dot-folders not walked; no-conventions fallback.
- **`ReplaceInNote`:** replaces a unique match; throws/errors clearly on 0 and on >1 matches;
  path-safety; round-trips content correctly.
- **Wiring:** the sub-agent is built with the editor tool set and **excludes** reminder/notify tools;
  effort is high; `ObsidianTools.AsTools()` no longer exposes `write_note`/`append_note` but still
  exposes `add_todo`.

## Out of scope

- Multi-note orchestration / batch jobs (the 1% case).
- Changing how voice-memo/recipe workflows write.
- Any change to the panel-authored system prompt (the tool `[Description]` carries the trigger
  vocabulary; Erda's prompt is unchanged).
