# Erda — a personal agent on the Microsoft Agent Framework (.NET) with DevUI

Erda is a lean MVP personal assistant built on the **Microsoft Agent Framework (MAF)** for
.NET, with MAF's browser-based **DevUI** as the interaction surface. It does three things:

1. **Chat** — talk to Erda in DevUI.
2. **Browse + edit an Obsidian vault** — list, read, search, write, and append notes.
3. **Voice-memo pipeline** — an Apple Voice Memo `.m4a` → OpenAI speech-to-text → a **Codex**
   agent (your ChatGPT subscription) cleans it up → the result is written into your vault.
   This is modeled as a MAF **workflow**.

## The three-credential model (the whole point of the design)

Erda deliberately uses **three separate credential contexts**. Keeping them apart is the
reason this project exists:

| Capability | Runs on | Auth | Credential |
|---|---|---|---|
| **Chat agent** (`gpt-5-mini`) | Azure AI Foundry, via the Azure OpenAI client | API key | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` |
| **Transcription** (`gpt-4o-transcribe`) | OpenAI platform | API key (pay-per-token) | `OPENAI_API_KEY` |
| **Codex** (`gpt-5.5`) | ChatGPT **subscription**, via the `codex` CLI | logged-in session in `~/.codex` | *(none in this app)* |

> **Hard rule, enforced in code:** `OPENAI_API_KEY` is **stripped** from the Codex subprocess
> environment (`ProcessStartInfo.Environment.Remove("OPENAI_API_KEY")` in
> [`Services/CodexRunner.cs`](Services/CodexRunner.cs)). This forces Codex to authenticate with
> the ChatGPT subscription instead of falling back to per-token API billing. On every launch
> Erda logs the command and `OPENAI_API_KEY absent from child env: True`.

The Azure key and the OpenAI-platform key are **different keys** — don't conflate them.

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` → `10.0.x`).
- **`codex` CLI** installed and logged in via your ChatGPT subscription (`codex --version`;
  auth lives in `~/.codex`). Verify with `codex exec -m gpt-5.5 "hello"`.
- A **gpt-5-mini deployment in Azure AI Foundry**, and the endpoint + key from the portal.
- An **OpenAI platform API key** (only used for transcription).
- An **Obsidian vault** to point at (defaults to `/Users/phil/TestingNotes`).

### Set the environment variables

These are read at runtime (not committed anywhere). Export them in the shell you run from:

```bash
export AZURE_OPENAI_ENDPOINT="https://<your-foundry-resource>.openai.azure.com/"
export AZURE_OPENAI_API_KEY="<your-foundry-key>"
export OPENAI_API_KEY="sk-...your-openai-platform-key..."
```

Erda starts even if these are unset (so DevUI loads), but the relevant capability will fail
with a clear message until the key is present. The startup log prints which are `set` / `MISSING`.

## Run

```bash
cd Erda
dotnet run
```

Then open the DevUI URL printed in the console, e.g. **`http://localhost:5167/devui`**
(the exact port comes from `Properties/launchSettings.json`; `/` redirects to `/devui`).

DevUI is only mounted in the **Development** environment (it exposes system prompts), guarded by
`app.Environment.IsDevelopment()`.

## Architecture: Erda is the single orchestrator

DevUI shows **one** entity, **`erda`** — a MAF `ChatClientAgent` on gpt-5-mini that owns the
agent loop. Everything else is a **tool** Erda routes to:

- the five **Obsidian vault** tools;
- **`process_voice_memo`** — the voice-memo MAF *workflow*, exposed as a tool via `AsAIFunction`
  (agent-as-tool), so Erda runs the real Transcribe→Codex→Obsidian pipeline rather than a
  standalone agent;
- **`consult_codex`** — Codex (gpt-5.5, high effort) **with live web search**, on the ChatGPT
  subscription. This is Erda's source of truth for facts and its tool for hard reasoning.

Because gpt-5-mini has limited/stale knowledge, Erda is instructed to **ground first**: any
request to explain/summarize/write-about a topic, technology, product, person, or event calls
`consult_codex` (which searches the web and cites sources) *before* writing — rather than
answering from memory. This is what keeps notes accurate instead of hallucinated.

## Using it in DevUI

- **Chat** — select **`erda`** and talk to it.
- **Vault tools** — ask Erda to list/read/search/write/append notes. All paths are confined to
  the vault root; anything that escapes is rejected.
- **Voice memo** — ask Erda to process a voice memo and give it the **absolute path** to a real
  `.m4a` file (e.g. *"process my voice memo at /Users/you/Recording.m4a"*). Erda calls
  `process_voice_memo`, which runs the workflow (transcribe → Codex → write) and saves the note to
  `VoiceMemos/<yyyy-MM-dd-HHmmss>.md`.
- **Facts / research / hard reasoning** — just ask normally (*"write a note about how X works"*).
  Erda grounds via `consult_codex` (web search) and writes a cited note.

## Configuration

`appsettings.json` (`Erda` section) plus the three environment variables above.

| Setting | Default | Purpose |
|---|---|---|
| `Erda:ChatDeployment` | `gpt-5-mini` | Foundry deployment name for the chat model |
| `Erda:TranscribeModel` | `gpt-4o-transcribe` | OpenAI STT model (`gpt-4o-mini-transcribe` is cheaper) |
| `Erda:CodexModel` | `gpt-5.5` | Model passed to `codex exec -m` (must be a model the ChatGPT subscription supports; `gpt-5-codex` is API-only) |
| `Erda:CodexReasoningEffort` | `high` | `codex exec -c model_reasoning_effort` |
| `Erda:VaultPath` | `/Users/phil/TestingNotes` | Obsidian vault root Erda may read/write |
| `Erda:VoiceMemoSubfolder` | `VoiceMemos` | Where processed memos are saved |

### Point it at a different vault

Edit `Erda:VaultPath` in `appsettings.json`, or override without editing files:

```bash
Erda__VaultPath="/Users/you/MyVault" dotnet run
```

(The double underscore is the .NET convention for nesting config sections in env vars.)

## Project layout

```
Erda/
  Program.cs                          # host + DI + DevUI wiring (registers only the erda agent)
  Configuration/ErdaOptions.cs        # strongly-typed settings
  Agents/ErdaAgent.cs                 # orchestrator agent: instructions + tool wiring
  Tools/ObsidianTools.cs              # the 5 vault function tools
  Tools/ReasoningTools.cs             # consult_codex tool (Codex + web search)
  Services/VaultService.cs            # path-safe file IO under VaultPath
  Services/Transcriber.cs             # OpenAI audio transcription (OPENAI_API_KEY)
  Services/CodexRunner.cs             # codex exec wrapper; strips OPENAI_API_KEY; optional web search
  Workflows/VoiceMemoWorkflow.cs      # the workflow + CreateTool (AsAIFunction) + note writer
  Workflows/Executors/                # VoiceMemoInput (chat adapter), Transcribe, Codex, ObsidianWrite
```

## Notes on the MAF API (verified against the installed packages)

MAF is in active preview; a few names differ from older docs/samples. As built here against the
1.8.0 train (May 2026):

- The chat agent uses `new AzureOpenAIClient(uri, new ApiKeyCredential(key)).GetChatClient(deployment).AsAIAgent(...)`.
  Azure.AI.OpenAI 2.x uses **`System.ClientModel.ApiKeyCredential`**, not `Azure.AzureKeyCredential`.
- DevUI transport is registered on the **builder**: `builder.AddOpenAIResponses()` /
  `builder.AddOpenAIConversations()` (extensions on `IHostApplicationBuilder`), then
  `app.MapOpenAIResponses()` / `app.MapOpenAIConversations()` / `app.MapDevUI()`.
- **Workflow-as-tool**: the voice-memo workflow is wrapped with `workflow.AsAIAgent(...)` then
  `.AsAIFunction(...)` and attached to Erda as the `process_voice_memo` tool — the orchestrator
  routes to it rather than it being a separate top-level agent.
- **A workflow hosted as an agent must speak the chat protocol.** When a workflow runs as an
  `AIAgent`, its start executor must accept `List<ChatMessage>` + `TurnToken` and its output must
  be a `ChatMessage`. So the voice-memo chain is bookended by a `ChatProtocolExecutor` input
  adapter (chat → path string) and a `ChatMessage`-returning terminal; the middle steps stay plain
  string executors. (A plain `string` start executor fails with "Workflow does not support
  ChatProtocol".) `AsAIAgent(..., includeWorkflowOutputsInResponse: true)` surfaces the terminal
  message as the response.
- **Codex model** must be one the ChatGPT subscription supports — `gpt-5.5`, not `gpt-5-codex`
  (the latter is API-only and Codex rejects it on a ChatGPT account).
- **Codex web search**: `codex exec -c tools.web_search=true` (with a read-only sandbox) enables
  the native web_search tool — this is how `consult_codex` grounds and cites facts.
- **An agent's `name` must equal its registration key** (e.g. both `"erda"`), or DevUI's eager
  entity enumeration throws at startup.

## Package versions

`Microsoft.Agents.AI` / `.OpenAI` / `.Workflows` `1.8.0` (stable);
`Microsoft.Agents.AI.Hosting` `1.8.0-preview`, `.Hosting.OpenAI` `1.8.0-alpha`,
`.DevUI` `1.8.0-preview`; `Microsoft.Extensions.AI` `10.6.0`;
`Azure.AI.OpenAI` `2.9.0-beta.1`; `OpenAI` `2.10.0`. See `Erda.csproj`.

## Deploy on a Jetson (Docker + Komodo)

Erda runs on an always-on ARM64 Linux box (e.g. an NVIDIA Jetson) as a two-container Compose
stack: **`erda`** + **`whatsapp-bridge`**. Nothing here uses the GPU — every model call is cloud
— so no `nvidia-docker` runtime is needed. In production Erda runs
`ASPNETCORE_ENVIRONMENT=Production`, so **DevUI is not mounted and no web port is published**;
you talk to Erda over WhatsApp.

### Files

- [`Dockerfile`](Dockerfile) — Erda image; installs the `codex` `aarch64` binary and sets
  `CODEX_HOME=/codex`.
- [`whatsapp-bridge/Dockerfile`](whatsapp-bridge/Dockerfile) — the Go bridge (static →
  distroless).
- [`docker-compose.yml`](docker-compose.yml) — the stack: a private network, a **shared
  `/media`** volume (the bridge writes downloaded media and hands Erda the absolute path), a
  persistent `bridge-data` volume (the WhatsApp session), and host bind-mounts for `~/.codex`
  and the vault.
- [`.env.example`](.env.example) — copy to `.env` and fill in paths + secrets.

### Three credential contexts in the container

The [three-credential model](#the-three-credential-model-the-whole-point-of-the-design) is
preserved: Azure + OpenAI-platform keys arrive as env vars; **Codex auth is the mounted
`~/.codex` session** (no key). `CodexRunner` still strips `OPENAI_API_KEY` from the subprocess,
so Codex authenticates against the ChatGPT subscription, not per-token billing.

### One-time bootstrap on the Jetson

1. **Codex login** (host): `codex login` — opens a device-code flow (prints a URL to open on
   another machine over SSH) and populates `~/.codex`. Point `CODEX_DIR` in `.env` at it.
2. **Configure**: `cp .env.example .env` and fill in. `CODEX_DIR` and `VAULT_DIR` must be
   **absolute** paths (compose does not expand `~`).
3. **Link WhatsApp** (first run only): `docker compose run --rm whatsapp-bridge`, then scan the
   QR via *WhatsApp → Linked devices → Link a device*. The session persists in the `bridge-data`
   volume; later starts connect silently. Ctrl-C once linked.
4. **Up**: `docker compose up -d --build`.

### Komodo

Point a Komodo **Stack** at this repo and let it run `docker compose up -d --build` on push
(webhook). Images build natively on the Jetson (arm64); bump `CODEX_VERSION` in the `Dockerfile`
to upgrade the CLI. `restart: unless-stopped` keeps both services up across reboots. Provide the
`.env` values as Komodo environment/secrets.

### Notes

- **Obsidian sync** must run on the host (Syncthing / obsidian-git / etc.) into `VAULT_DIR`; the
  container only reads/writes files — it does not sync them.
- **Voice memos**: forward the audio to Erda over WhatsApp. The bridge downloads it into the
  shared `/media` volume and Erda transcribes it from there.
- **Seq**: the error-watch scheduler ships inside the `erda` container and reads from the central
  `SEQ_SERVER_URL` (default `https://seq.phib.io`). No Seq container is included.
