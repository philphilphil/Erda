# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
make dev          # run Erda locally (dotnet run; DevUI at http://localhost:5167/devui)
make dev-wa       # run Erda + WhatsApp bridge together (Ctrl-C kills both); needs node/npx
make deploy       # git pull && docker compose up -d --build (server only)

dotnet test       # run all tests (from repo root)
dotnet test --filter "ClassName=ErrorSignatureTests"  # run a single test class
dotnet build      # build without running
```

Tests live in `Erda.Tests/` (xUnit). The test project references the main `Erda.csproj`.

## Architecture

Erda is a **.NET 10 web app** built on the **Microsoft Agent Framework (MAF) 1.8.0**. It runs as a single orchestrator agent (`erda`) that routes to tools and a workflow.

### The three-credential model

| Capability | Client | Key |
|---|---|---|
| Chat agent (`gpt-5-mini`) | `AzureOpenAIClient` | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` |
| Transcription (`gpt-4o-transcribe`) | OpenAI SDK | `OPENAI_API_KEY` |
| Codex (`gpt-5.5`) | `codex` CLI subprocess | ChatGPT subscription session in `~/.codex` |

**Critical:** `CodexRunner.cs` strips `OPENAI_API_KEY` from the Codex subprocess environment so Codex authenticates via ChatGPT subscription, not per-token billing. Never remove this stripping.

### Key source files

- `Program.cs` — host/DI wiring: Serilog, OpenTelemetry, MAF, DevUI, WhatsApp endpoints, background services
- `Agents/ErdaAgent.cs` — orchestrator: system prompt + tool registration (vault tools, `consult_codex`, `process_voice_memo`)
- `Agents/ErdaAgentResponder.cs` — adapts agent turns for the WhatsApp channel
- `Tools/ObsidianTools.cs` — 5 vault tools (list/read/search/write/append); paths confined to `VaultPath`
- `Tools/ReasoningTools.cs` — `consult_codex` tool: launches Codex with web search enabled
- `Services/CodexRunner.cs` — `codex exec` subprocess wrapper; strips `OPENAI_API_KEY`; optional web search
- `Services/VaultService.cs` — path-safe file I/O under `VaultPath`
- `Services/Transcriber.cs` — OpenAI audio transcription
- `Workflows/VoiceMemoWorkflow.cs` — voice memo pipeline (transcribe → Codex → write); wrapped as `process_voice_memo` tool via `AsAIFunction`
- `Scheduling/ErrorWatchScheduler.cs` — background loop: polls Seq for errors, deduplicates by signature, analyzes with Codex, alerts via WhatsApp
- `WhatsApp/` — bridge integration: inbound queue, background worker, channel service (dispatches text/voice/image to the agent), sender

### MAF-specific patterns

- Chat agent: `new AzureOpenAIClient(uri, new ApiKeyCredential(key)).GetChatClient(deployment).AsAIAgent(...)`. Uses `System.ClientModel.ApiKeyCredential`, **not** `Azure.AzureKeyCredential`.
- DevUI is registered on the builder: `builder.AddOpenAIResponses()` / `builder.AddOpenAIConversations()`, then `app.MapOpenAIResponses()` / `app.MapOpenAIConversations()` / `app.MapDevUI()`. Only mounted in the `Development` environment.
- Workflow-as-tool: the voice-memo workflow is `workflow.AsAIAgent(...).AsAIFunction(...)`. Its start executor must accept `List<ChatMessage>` + `TurnToken` (a plain `string` start executor fails with "Workflow does not support ChatProtocol").
- **Agent `name` must match its registration key** (both `"erda"`), or DevUI throws at startup.

### Observability

OpenTelemetry traces exported over OTLP to Seq (`{Seq:ServerUrl}/ingest/otlp/v1/traces`). Every span is tagged `app = Erda` — filter `app = 'Erda'` in Seq (not `service.name`, which lands under `@ra` and is not filterable). Content capture (prompts, tool args) is off in production, on in Development.

### WhatsApp channel

The `whatsapp-bridge` (Go) handles the WhatsApp socket and posts inbound messages to `POST /whatsapp/inbound`. `WhatsAppInboundWorker` drains the queue and calls `WhatsAppChannelService`, which enforces the owner whitelist and dispatches by message type. The bridge and Erda share a `/media` Docker volume for downloaded audio/images.

### Production deployment

Docker Compose stack on an ARM64 Jetson: `erda` + `whatsapp-bridge` containers. Codex auth is a bind-mounted `~/.codex` session. In `Production`, `ASPNETCORE_ENVIRONMENT=Production` disables DevUI; WhatsApp is the only interaction surface. Managed by Komodo (webhook → `docker compose up -d --build`).

## Configuration reference

`appsettings.json` (`Erda` section) + env vars. Key settings not in README:

| Section | Key | Purpose |
|---|---|---|
| `WhatsApp` | `OwnerNumber`, `BridgeUrl`, `SharedSecret` | Bridge integration; only messages from `OwnerNumber` are processed |
| `ErrorWatch` | `Enabled`, `PollInterval`, `MinLevel`, `MaxAlertsPerPoll` | Error-watch scheduler behavior |
| `Seq` | `ServerUrl`, `ApiKey`, `IngestToErda` | Seq sink for Serilog + OTLP target |
| `Observability` | `Enabled`, `CaptureMessageContent` | OTel master switch; content capture gate |
