# Design: `delegate_vault_task` — codex working directly on the vault

**Date:** 2026-06-25
**Status:** Approved (design), pending implementation plan

## Problem

Today erda can only involve codex in vault work by reading note contents itself
(via `ObsidianTools`) and passing them as the `context` string to `consult_codex`.
That is inefficient: it burns erda's (small-model) context window, and it limits
codex to the specific notes erda named — codex cannot grep, traverse, or edit the
vault on its own. `consult_codex` is deliberately a *stateless oracle*: it runs in a
throwaway temp dir with `--sandbox workspace-write` and its developer instruction
forbids file edits.

We want erda to be able to hand codex a natural-language task and let codex work on
the vault directly: read, search (grep/rg), create, and edit notes with its own shell.

## Decisions (locked)

- **Access level:** read-write. Codex can create/edit/delete `.md` notes.
- **Scope:** whole vault (`VaultPath` as the working root).
- **Tool name:** `delegate_vault_task`.
- **Kill-switch:** none — always-on, no new config (YAGNI). Reuses existing `VaultPath`.
- **Additive:** `consult_codex` is unchanged (stateless oracle, no filesystem).

## Mechanism (verified against codex-cli 0.139.0)

`codex exec` already supports exactly what we need:
- `-C, --cd <DIR>` — set the agent's working root.
- `-s, --sandbox workspace-write` — makes the cwd writable to model-run shell commands.
- `--add-dir <DIR>` — additional writable directory.
- `-o, --output-last-message <FILE>` — where the final message is written.

Pointing `--cd` at `VaultPath` under `workspace-write` makes the entire vault
readable and writable to codex's shell. In Docker the vault is already mounted into
the `erda` container (the same named volume `obsidian-sync` uses), so this works in
production unchanged.

## Components

### 1. `CodexRunner` — new method `RunVaultTaskAsync`

Add `RunVaultTaskAsync(string task, string? reasoningEffort = null, CancellationToken ct = default)`.

Refactor so `RunPromptAsync` (oracle) and `RunVaultTaskAsync` (vault) share one
private core that takes an explicit `(workingRoot, scratchDir, developerInstruction,
enableWebSearch, ...)`. The core keeps everything that already exists: the
`OPENAI_API_KEY` strip, `preferred_auth_method="chatgpt"`, timeout/kill,
auth-failure detection, and logging.

Two directories, separated:
- **scratch dir** — always a fresh `Directory.CreateTempSubdirectory("erda-codex-")`.
  Holds the `-o codex-final.txt` output. **It is the only directory deleted in the
  `finally`.**
- **working root** — for the vault task, `options.Value.VaultPath`; passed as `--cd`.
  For the oracle, the scratch dir (current behavior preserved).

Sandbox stays `workspace-write`. Pass `--add-dir <scratchDir>` so writing the output
file is always permitted even when cwd is the vault. Keep `--skip-git-repo-check` (the
vault may or may not be a git repo).

**Shell network egress stays ON (`network_access=true`) for both paths.** The adversarial
review suggested gating it off for vault tasks (to stop an injected note exfiltrating vault
contents), and that was tried — but it broke in the deployment container: codex enforces
`network_access=false` by building a **network namespace via bubblewrap**, and `bwrap` is not
installed in the image (`"bubblewrap is unavailable: no system bwrap was found on PATH"`).
codex's filesystem confinement uses Landlock (no bwrap), so the oracle and the vault's
read/write confinement still work; only the *network-blocking* step needs bwrap. The review
rated the exfiltration risk **low** for this single-owner, owner-whitelisted LAN tool, so
egress is left on. (To re-enable the hardening later: install `bubblewrap` in the runtime
image and confirm the container permits unprivileged user namespaces.)

**Critical invariant:** the path passed to `Directory.Delete(...)` in the `finally`
is always the temp scratch dir, never the working root. The refactor must make it
structurally impossible to delete `VaultPath` — the `finally` only ever closes over
`scratchDir`.

### 2. `ReasoningTools` — new tool `delegate_vault_task`

Lives in `ReasoningTools` (already has `CodexRunner` injected). `CodexRunner`
already holds `IOptions<ErdaOptions>`, so it reads `VaultPath` internally — the path
never leaves Core, and the tool signature stays minimal.

Signature:
```csharp
delegate_vault_task(
    string task)                       // self-contained natural-language instruction
```

Reasoning effort is **fixed at `high`** (the ceiling `CodexRunner` accepts) — vault work
favors quality over speed, and the model is not given a knob to lower it. Pinned by a
tool-level test (`ReasoningToolsTests`).

Its own developer instruction (distinct from the oracle's "do not edit files")
**defers to the vault's own `AGENTS.md`** rather than hardcoding conventions. Because
codex runs with `--cd <VaultPath>`, the vault root `AGENTS.md` is codex's cwd and is
auto-discovered as project instructions; the instruction makes that explicit and tells
codex to also honor nearer per-folder `AGENTS.md` files. The vault `AGENTS.md` is the
single source of truth (review-vs-writing mode, CriticMarkup threading, inbox naming,
search globs), so the tool does not duplicate those rules:

> You are working directly inside Phil's Obsidian vault — a tree of Markdown notes —
> which is your current working directory. FIRST read the `AGENTS.md` at the vault root
> and follow it (review vs. writing mode, CriticMarkup conventions, where files may be
> created, how to search). Also honor any nearer `AGENTS.md` in a note's own folder —
> the nearer file wins. Use your shell tools (rg/grep/cat) to read and search notes,
> and do not touch files outside the vault. When done, reply with a concise Markdown
> summary of exactly what you read and what you changed (list the file paths).

The instruction references `AGENTS.md` by its **relative** name, never the host path,
so it stays correct inside the Docker container where the vault mounts at a different
absolute path. Do not touch files outside the vault.

The `[Description]` attribute carries the routing guidance the model sees:
delegate multi-note review / cleanup / refactor tasks that benefit from codex's
reasoning + direct filesystem access; it can read and edit the vault itself, so erda
should NOT pre-fetch note contents for it.

Web search: left **on** (review tasks may need fact-checking), matching `consult_codex`.

### 3. Registration

No change needed in `ErdaAgent.cs` beyond what already wires `ReasoningTools` —
`tools.AddRange(services.GetRequiredService<ReasoningTools>().AsTools())` already
runs, so adding the new `AIFunctionFactory.Create(...)` entry inside
`ReasoningTools.AsTools()` is sufficient.

### 4. System-prompt routing (optional, panel-side)

erda's system prompt lives in the SQLite DB (authored in the control panel), not in
code. The tool's `[Description]` is the primary "when to use" signal and works with
no prompt change. Optionally, Phil can add one line to the panel system prompt to
make the three-way routing explicit:
- `ObsidianTools` — simple single-note reads/writes erda does itself.
- `delegate_vault_task` — multi-note review/cleanup/refactor handed to codex.
- `consult_codex` — external/world-knowledge questions (no vault).

This is a documentation note, not a code deliverable.

## Data flow

```
Phil (WhatsApp): "review my Projects notes for stale TODOs and tidy them up"
  → erda calls delegate_vault_task(task, effort)
    → CodexRunner runs: codex exec --cd <VaultPath> --sandbox workspace-write
        --add-dir <scratch> -o <scratch>/codex-final.txt ... <prompt>
      → codex greps/reads/edits notes in the vault itself
    → returns Markdown summary of what it changed
  → erda relays the summary to Phil
obsidian-sync propagates the edits; Obsidian Sync keeps version history.
```

## Safety

- Codex now mutates the **live** vault. Recoverability rests on Obsidian Sync's
  version history (and any git history the vault has). Acceptable per decision.
- **Shell network egress stays on** (see above) — gating it off needs bubblewrap,
  which the container lacks. Exfiltration risk accepted as low for this LAN tool.
- `CodexTimeout` already bounds each run; a stuck codex is killed (process tree).
- Concurrency: two simultaneous vault tasks could race on one file — no worse than
  two concurrent `write_note` calls. Not gating.
- The `OPENAI_API_KEY` strip and ChatGPT-subscription auth are unchanged and must
  remain (shared core — applied to both paths in `RunCoreAsync`).
- The `finally` cleanup deletes only the throwaway scratch dir, never the working
  root, so the vault can never be removed by a run.

## Testing

`CodexRunner` shells out to a real binary, so we drive it end-to-end with a fake-codex
shell script (the existing test pattern). The fake dumps its full argv into the `-o`
file, so a test can assert exactly how codex was invoked **and** exercise the real `-o`
output roundtrip. Two tests pin the invariants that matter:

1. **Vault task** — `--cd` == `VaultPath`; `--add-dir` == the scratch dir (which lives
   outside the vault); `network_access=true`; the vault and its notes survive; and the
   scratch dir **is** deleted afterward.
2. **Oracle path** — no `--add-dir`; `--cd` is a fresh `erda-codex-*` temp dir, never
   the vault; `network_access=true` (unchanged); scratch dir is deleted.

These were hardened in response to the adversarial review, which mutation-tested the
first draft and showed that dropping `--add-dir` left the test green.

## Out of scope

- Per-subfolder restriction, approval-before-write gating, and a config kill-switch
  were considered and explicitly deferred (YAGNI).
- No change to `consult_codex` or the voice-memo `CodexExecutor`.
