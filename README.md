# Erda — a personal agent on the Microsoft Agent Framework (.NET)

Erda is a lean MVP personal assistant built on the **Microsoft Agent Framework (MAF)** for
.NET. You interact with it through a **WhatsApp** channel and a **LAN control panel** (a Vue
SPA). It does three things:

1. **Chat** — talk to Erda in the control panel's chat view or over WhatsApp.
2. **Browse + edit an Obsidian vault** — list, read, search, write, and append notes.
3. **Voice-memo pipeline** — an Apple Voice Memo `.m4a` → OpenAI speech-to-text → a **Codex**
   agent (your ChatGPT subscription) cleans it up → the result is written into your vault.
   This is modeled as a MAF **workflow**.

## The three-credential model (the whole point of the design)

Erda deliberately uses **three separate credential contexts**. Keeping them apart is the
reason this project exists:

| Capability | Runs on | Auth | Credential |
|---|---|---|---|
| **Chat agent** (`gpt-5.4-mini`) | Azure AI Foundry `/openai/v1`, via the stock OpenAI SDK | API key | `AZURE_OPENAI_ENDPOINT` (the `…/openai/v1` URL) + `AZURE_OPENAI_API_KEY` |
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

Erda starts even if these are unset (so the app still boots), but the relevant capability will fail
with a clear message until the key is present. The startup log prints which are `set` / `MISSING`.

## Run

```bash
make dev                            # backend (:5167) + control-panel SPA (:5173)
dotnet run --project Erda.Server    # bare backend only, no SPA
```

Then open the control panel at **`http://localhost:5173`** (the Vite dev server proxies `/api`
to the backend on :5167). See [Control panel](#control-panel-vue-spa--json-api) below.

## Control panel (Vue SPA + JSON API)

Erda hosts a single-user, **LAN-only** web control panel for managing reminders + scheduled
prompts, editing the system prompt (with versioned rollback), watching a live activity feed, and
tweaking runtime config — all over a JSON API under `/api`, with a Vue 3 SPA front end in `web/`.

- **Reminders are live** (the scheduler reads the DB each tick); **prompt + config edits apply on
  restart** (a one-click *Restart Erda* button). The activity feed is pushed live over
  Server-Sent Events.
- **Auth is off by default** — the panel is open on the LAN. Set `Panel__Password` in `.env` to
  require a single-user cookie login (`Panel__Username` defaults to `admin`).
- **Local dev:** run the backend and the Vite dev server together:

  ```bash
  make dev            # backend (:5167) + Vite (:5173); open http://localhost:5173
  # add the WhatsApp bridge with `make dev-all`; SPA on its own with `make web`
  ```

  Vite proxies `/api` to the backend, so cookies work same-origin. Build the SPA with
  `cd web && npm ci && npm run build`.
- **Production:** the Dockerfile builds the SPA and serves it from the app's `wwwroot` at the root
  URL; the API is at `/api`.

## Architecture: Erda is the single orchestrator

Erda is **one** agent — **`erda`**, a MAF `ChatClientAgent` on gpt-5-mini that owns the
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

## Using it

- **Chat** — open the control panel's **Chat** view, or message Erda on WhatsApp.
- **Vault tools** — ask Erda to list/read/search/write/append notes. All paths are confined to
  the vault root; anything that escapes is rejected.
- **Voice memo** — ask Erda to process a voice memo and give it the **absolute path** to a real
  `.m4a` file (e.g. *"process my voice memo at /Users/you/Recording.m4a"*). Erda calls
  `process_voice_memo`, which runs the workflow (transcribe → Codex → write) and saves the note to
  `VoiceMemos/<yyyy-MM-dd-HHmmss>.md`.
- **Facts / research / hard reasoning** — just ask normally (*"write a note about how X works"*).
  Erda grounds via `consult_codex` (web search) and writes a cited note.

## Configuration

**Env-only, no defaults.** There is no `appsettings.json` — every setting comes from environment
variables, kept in `.env` (see [`.env.example`](.env.example) for the full, documented catalog).
`make dev` sources `.env`; in prod `docker-compose` loads it via `env_file`. **No setting has an
in-code default**: a missing value stops the app at startup naming the key (validated via
`ValidateOnStart`). Bool switches are **off when absent**. Feature settings (WhatsApp, ErrorWatch,
Reminders, Browser) are required only when that feature's `Enabled` switch is on.

Always required: the 3 credentials, `Erda__VaultPath`, `Erda__DbPath`, and the model/codex settings
(`Erda__ChatDeployment`, `Erda__TranscribeModel`, `Erda__CodexModel`, `Erda__CodexReasoningEffort`,
`Erda__CodexTimeout`, `Erda__CodexExecutable`, `Erda__VoiceMemoSubfolder`). The only values *not* in
config are a handful of fixed mechanics expressed as code constants — the Playwright MCP command/args,
the browse-loop cap, and the 1Password vault name (`Erda`) — which aren't settings to tune. The double
underscore is the .NET convention for nesting config sections in env vars (`Erda__VaultPath` →
`Erda:VaultPath`).

### Point it at a different vault

Set `Erda__VaultPath` in `.env`, or override ad-hoc:

```bash
Erda__VaultPath="/Users/you/MyVault" dotnet run --project Erda.Server
```

## Project layout

```
Erda/
  Program.cs                          # host + DI wiring (registers only the erda agent)
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

- The chat agent uses the **stock OpenAI SDK** pointed at Azure's unified `/openai/v1` surface:
  `new ChatClient(model: deployment, credential: new ApiKeyCredential(key), options: new OpenAIClientOptions { Endpoint = new Uri(azureV1Url) }).AsAIAgent(...)`.
  We dropped `Azure.AI.OpenAI` — its dated `api-version` is what made newer Azure models (e.g.
  `gpt-5.4-mini`) fail with "API version not supported", and the OpenAI client is provider-portable
  (swap endpoint+key+deployment for any OpenAI-compatible backend). Uses
  **`System.ClientModel.ApiKeyCredential`**. We use **Chat Completions, not the Responses API**, because
  Chat Completions is the universal OpenAI-compatible surface (Responses is OpenAI/Azure-only).
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
- **The agent is registered and resolved by keyed DI under its name** — `ErdaAgent.Name` is
  `"erda"`, and `ErdaAgentResponder` / `WebChatService` pull it via `[FromKeyedServices("erda")]`.

## Package versions

`Microsoft.Agents.AI` / `.OpenAI` / `.Workflows` `1.8.0` (stable);
`Microsoft.Agents.AI.Hosting` `1.8.0-preview`; `Microsoft.Extensions.AI` `10.6.0`;
`OpenAI` `2.10.0` (the chat client, pointed at Azure `/openai/v1`; `Azure.AI.OpenAI` was removed). See the `Erda.*/*.csproj` project files (MAF hosting lives in `Erda.Server`, the MAF agent packages in `Erda.Agents`, EF/OpenAI in `Erda.Core`).

## Observability

Erda emits OpenTelemetry traces for every turn — a span tree of **agent run → model call (token
usage) → each tool/function call** (`consult_codex`, the vault tools, `process_voice_memo`), with
durations and errors. The agent is instrumented via MAF's `AsBuilder().UseOpenTelemetry()` on the
`Erda.Agent` activity source ([`Agents/ErdaAgent.cs`](Agents/ErdaAgent.cs)); the pipeline is wired
in [`Program.cs`](Program.cs).

- **Where:** exported over OTLP to the Seq you already run (`{Seq:ServerUrl}/ingest/otlp/v1/traces`,
  reusing `Seq:ApiKey`). In Development a console exporter is also enabled for a zero-setup view.
- **Finding them in Seq:** every span is tagged `app = Erda` (a span attribute, so it's filterable
  like the Serilog logs — OTLP *resource* attributes such as `service.name` land under `@ra` and
  are **not** filterable, which is why a `service.name = 'Erda'` search finds nothing). Filter
  `app = 'Erda'` to see logs + traces together, or `gen_ai.operation.name = 'invoke_agent'` for just
  the turns; click a span's trace icon for the waterfall. **Tool calls aren't separate spans** —
  they appear inside the `invoke_agent` span's `gen_ai.input.messages` / `gen_ai.output.messages`
  (as `tool_call` / `tool_call_response` parts), visible only when content capture is on. `chat`
  spans carry the token counts.
- **Privacy:** prompt / completion / tool-argument **content** is captured only when
  `Observability__CaptureMessageContent` is true (which sets the standard env var
  `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`). Leave it unset in production — Seq then gets
  tool names, timings, and token counts but no message text; the dev `.env` sets it true. The Codex
  launch log follows the same flag: it shows the actual question (not the system-prompt boilerplate)
  only when content capture is on.

| Setting | Absent ⇒ | Purpose |
|---|---|---|
| `Observability__Enabled` | off | Master switch for OpenTelemetry tracing (set `true` to enable) |
| `Observability__CaptureMessageContent` | off | Capture prompts + tool args in spans and the Codex log (dev `.env` sets `true`) |

## Deploy on a homeserver (Docker + Komodo)

Erda runs on an always-on Linux box as a self-contained three-container Compose stack: **`erda`** +
**`whatsapp-bridge`** + **`obsidian-sync`**. Nothing here uses the GPU — every model call is cloud —
so no `nvidia-docker` runtime is needed. In production Erda runs `ASPNETCORE_ENVIRONMENT=Production`;
you interact with Erda over WhatsApp and through the **LAN control panel** (published on port 5167 —
see below).

The image arch defaults target **amd64**; for an ARM64 host (e.g. the old Jetson) override the build
ARGs in [`Dockerfile`](Dockerfile) (`CODEX_TARGET=aarch64-unknown-linux-musl`, `OP_ARCH=arm64`).
**The vault syncs inside the stack** — the `obsidian-sync` sidecar runs Obsidian's official headless
Sync client against a shared `vault` volume, so the host needs nothing but Docker (no Syncthing /
obsidian-git to set up). Requires an **Obsidian Sync** subscription.

### Files

- [`Dockerfile`](Dockerfile) — Erda image; installs the `codex` binary (amd64 by default) and sets
  `CODEX_HOME=/codex`.
- [`whatsapp-bridge/Dockerfile`](whatsapp-bridge/Dockerfile) — the Go bridge (static →
  distroless).
- [`obsidian-sync/`](obsidian-sync/) — the Obsidian Sync sidecar (Node + the official
  `obsidian-headless` client); keeps the shared `vault` volume synced. See its
  [`entrypoint.sh`](obsidian-sync/entrypoint.sh) for the `setup` / `login` / `sync` modes.
- [`docker-compose.yml`](docker-compose.yml) — the stack: a private network, a **shared `/media`**
  volume (the bridge writes downloaded media and hands Erda the absolute path), a shared **`vault`**
  volume (Erda reads/writes notes; `obsidian-sync` keeps it synced), persistent `bridge-data` /
  `obsidian-config` volumes (the WhatsApp + Obsidian sessions), and a host bind-mount for `~/.codex`.
- [`.env.example`](.env.example) — copy to `.env` and fill in paths + secrets.

### Three credential contexts in the container

The [three-credential model](#the-three-credential-model-the-whole-point-of-the-design) is
preserved: Azure + OpenAI-platform keys arrive as env vars; **Codex auth is the mounted
`~/.codex` session** (no key). `CodexRunner` still strips `OPENAI_API_KEY` from the subprocess,
so Codex authenticates against the ChatGPT subscription, not per-token billing.

### Browser logins (1Password)

Erda logs into sites using credentials it never sees. Set up a dedicated, least-privilege vault:

1. In the 1Password app, create a vault named **`Erda`**. Add a login item per site Erda may use
   (the item's **website** field drives matching; include the **one-time password** field for TOTP-based
   2FA). Curating this vault is how you control which sites Erda can sign into — Erda has no write access.
2. In 1Password → **Developer → Service Accounts**, create a service account with **read-only** access
   to **only** the `Erda` vault. Copy its token into `OP_SERVICE_ACCOUNT_TOKEN` in `.env`.
3. Set `Erda__Browser__Enabled=true`. On the first run for a site, Erda fills the login form from 1Password
   and the session persists on the `browser-data` volume, so later runs skip the login.

**Hard stops:** a captcha or a push/SMS/email challenge cannot be solved unattended — Erda stops and
messages you on WhatsApp. As a fallback you can refresh a session manually: run a headed browser against
the same profile and log in once, then let Erda reuse the persisted session.

### One-time bootstrap on the homeserver

1. **Codex login** (host): `codex login` — opens a device-code flow (prints a URL to open on
   another machine over SSH) and populates `~/.codex`. Point `CODEX_DIR` in `.env` at it.
2. **Configure**: `cp .env.example .env` and fill in. `CODEX_DIR` must be an **absolute** path
   (compose does not expand `~`), and set `OBSIDIAN_VAULT_NAME` to your exact Obsidian Sync vault name.
3. **Link WhatsApp** (first run only): `docker compose run --rm whatsapp-bridge`, then scan the
   QR via *WhatsApp → Linked devices → Link a device*. The session persists in the `bridge-data`
   volume; later starts connect silently. Ctrl-C once linked.
4. **Link Obsidian Sync** (first run only): `docker compose run --rm obsidian-sync setup` — logs in
   (email/password/MFA, or skipped if `OBSIDIAN_AUTH_TOKEN` is set) and links the vault, prompting for
   the E2E password if the vault is encrypted. State persists in the `obsidian-config` volume.
5. **Up**: `docker compose up -d --build`. On first start the vault volume fills from Obsidian Sync.

### Komodo

Point a Komodo **Stack** at this repo and let it run `docker compose up -d --build` on push
(webhook). Images build natively on the homeserver (amd64); bump `CODEX_VERSION` (or
`OBSIDIAN_HEADLESS_VERSION` in `obsidian-sync/Dockerfile`) to upgrade the CLIs. `restart:
unless-stopped` keeps the services up across reboots. Provide the `.env` values as Komodo
environment/secrets.

### Notes

- **Obsidian sync** runs inside the stack: the `obsidian-sync` sidecar keeps the shared `vault`
  volume continuously synced via Obsidian's official headless client. Erda just reads/writes the
  files; the sidecar uploads/downloads changes. (No more host-side Syncthing / obsidian-git.)
- **Voice memos**: forward the audio to Erda over WhatsApp. The bridge downloads it into the
  shared `/media` volume and Erda transcribes it from there.
- **Seq**: the error-watch scheduler ships inside the `erda` container and reads from the central
  `Seq__ServerUrl` (e.g. `https://seq.phib.io`); blank disables it. No Seq container is included.
