# Collapse Codex into Erda on a local OpenAI-compatible endpoint

- **Date:** 2026-06-25
- **Branch:** `experiment/codex-to-endpoint`
- **Status:** Design — awaiting review before implementation planning

## 1. Motivation

Today Erda is a **two-tier** system:

- **Erda** — the orchestrator agent on a *weak* model (`gpt-5.4-mini`, Azure OpenAI Chat Completions), which delegates hard work to…
- **Codex** — a *strong* agent (`gpt-5.5` via the ChatGPT subscription, run as a `codex` CLI subprocess) that reasons, touches the vault filesystem, and can web-search.

A local proxy at `http://127.0.0.1:10531/v1` exposes the **same** codex-oauth models (`gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`) over a plain **OpenAI-compatible HTTP** surface. That lets us **collapse the two tiers into one**: Erda itself becomes `gpt-5.5`, and everything Codex did becomes Erda's own in-process capability. No subprocess, no second agent.

## 2. Endpoint findings (empirically tested)

| Capability | Result |
|---|---|
| `GET /v1/models` | `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `codex-auto-review` (owned_by `codex-oauth`) |
| Chat Completions (text + tool calling) | ✅ works |
| Chat Completions web search | ❌ hosted `web_search` ignored |
| Responses API (`/v1/responses`) **non-streamed** | ⚠️ returns empty `output` — **broken on this proxy** |
| Responses API **streamed** | ✅ full, spec-compliant SSE event stream |
| Responses API web search (streamed) | ✅ `response.web_search_call.*` events; real current results with URLs; `web_search.num_requests: 1` |
| Auth | none required (loopback); SDK still needs a non-empty credential string |

**Conclusion:** native web search is available, but **only via the Responses API in streaming mode** — which is exactly how Codex itself works. The proxy accepted a literal `{"type":"web_search"}` tool, so the wire discriminator `web_search` is confirmed against this proxy.

## 3. Decisions (from brainstorming)

1. **Responses API everywhere** (Option A) — Erda runs on `OpenAI.Responses.ResponsesClient` against the endpoint, model `gpt-5.5`, with a `HostedWebSearchTool` so it browses natively. Drops the documented "Chat Completions for portability" stance — acceptable, we're committing to this endpoint.
2. **Rip Codex out completely** — the `codex` CLI (`CodexRunner`/`ICodexRunner`), `consult_codex`, and `delegate_vault_task` all go. The work they did moves *up* into Erda.
3. **Vault tools stay** — `ObsidianTools` (6 tools) remain as Erda's own hands on the vault; voice memos/reminders need vault I/O. (This overrides the original "remove the vault tool" ask.)
4. **Voice-memo mode is preserved** — pipeline is unchanged except the middle step: transcribe → ~~codex format~~ **model format (HTTP call)** → write `1 Inbox/`. Transcription stays on `OPENAI_API_KEY` (the endpoint has no transcribe model).
5. **WhatsApp goes streaming** — the responder switches from `RunAsync` to an aggregated `RunStreamingAsync` (the proxy's non-streamed Responses is broken), which also enables…
6. **Typing indicator** — a new bridge `POST /presence` → `SendChatPresence`, driven while Erda generates.
7. **Retire `ReminderRow.DirectToCodex`** — Erda now has native web search, so the "run this scheduled prompt straight through codex" branch collapses; scheduled prompts run through Erda. The column is dropped via an EF migration.

## 4. Architecture

**The seam:** `ICodexRunner`/`CodexRunner` (subprocess, `instruction+input → text`, optional web search) is replaced by a new in-process **`IReasoner`** backed by the Responses API (streamed, optional `web_search`). Every Codex consumer swaps to it — same shape, subprocess → HTTP.

```
                 ┌──────────────────────────────────────────────┐
  WhatsApp ─────▶│ ErdaAgentResponder (RunStreamingAsync, aggregated) │──┐
  Web chat ─────▶│ WebChatService (already streams)               │  │
                 └──────────────────────────────────────────────┘  │
                          Erda agent = ResponsesClient(gpt-5.5)      │
                          tools: ObsidianTools (vault) · voice-memo  │
                                 notify · reminders · browser        │
                                 + HostedWebSearchTool  ◀── new      │
                                                                     ▼
  voice-memo / recipe / error-watch / reminder ──▶ IReasoner ──▶ ResponsesClient (streamed)
```

### New files

**`Erda.Core/Services/IReasoner.cs`** — the seam replacing `ICodexRunner`:

```csharp
public interface IReasoner
{
    // Streamed Responses call collapsed to final text. webSearch toggles the web_search tool.
    // reasoningEffort == null ⇒ ErdaOptions.ChatReasoningEffort fallback (minimal/low/medium/high).
    Task<string> ReasonAsync(string prompt, bool webSearch = false,
        CancellationToken ct = default, string? logLabel = null, string? reasoningEffort = null);

    // Preserves CodexRunner.RunAsync semantics (webSearch ON, logLabel "voice-memo processing").
    Task<string> RunAsync(string developerInstruction, string transcript, CancellationToken ct = default);
}
```

**`Erda.Core/Services/ResponsesReasoner.cs`** — endpoint-backed impl. Uses the **streamed** Responses surface. Verified construction (names confirmed against installed assemblies):

```csharp
#pragma warning disable OPENAI001 // Responses surface is [Experimental]
var responses = new OpenAI.Responses.ResponsesClient(
    new ApiKeyCredential(chatApiKeyOrDummy),
    new OpenAIClientOptions { Endpoint = new Uri(chatBaseUrl) });
#pragma warning restore OPENAI001
```

Per call: build a one-shot agent via `responses.AsAIAgent(model: options.ChatModel, …, tools: webSearch ? [new HostedWebSearchTool()] : [])` and aggregate with `RunStreamingAsync(...).ToAgentResponseAsync(ct)` → `.Text`. Port `CodexRunner.NormalizeReasoningEffort` (null ⇒ configured default; valid `minimal/low/medium/high`). Must **not throw past callers** that degrade to a string (error-watch) or return false (scheduler).

> **Verified name corrections:** type is `OpenAI.Responses.ResponsesClient` (not `OpenAIResponseClient`); ctor takes **no model** (supply at `AsAIAgent(model:)`); streamed update type is `Microsoft.Agents.AI.AgentResponseUpdate`; aggregate via `ToAgentResponseAsync`; the whole Responses block needs `#pragma warning disable OPENAI001`.

## 5. File-by-file edits

### Erda.Agents
- **`Orchestration/ErdaAgent.cs`** — `:39-42` swap `new ChatClient(...)` → `ResponsesClient(...)`; `:60` `responses.AsAIAgent(model: options.ChatModel, instructions, name: Name, tools: tools)`. Tools `:44-52`: KEEP `ObsidianTools` (`:45`), voice-memo (`:46`), `NotifyTools`/`ReminderTools` (`:48-49`), browser (`:51-52`); **REMOVE** `ReasoningTools().AsTools()` (`:47`, drops `consult_codex` + `delegate_vault_task`); **ADD** `tools.Add(new HostedWebSearchTool());`. Keep the `.AsBuilder().UseOpenTelemetry(...).Use(...).Build()` chain (`:66-73`).
- **`Orchestration/BrowserAgent.cs`** — `:81-84` second `ChatClient` site, migrate to `ResponsesClient` in lockstep; `:73` deployment fallback repoints `ChatDeployment` → `ChatModel`. Must compile once Azure keys are gone.
- **`Tools/ReasoningTools.cs`** — **DELETE** (holds both removed tools).
- **`Workflows/MemoProcessor.cs`** — ctor `:16` `CodexRunner` → `IReasoner`; `:27` `reasoner.RunAsync(instruction, transcript, ct)`.
- **`Workflows/Executors/CodexExecutor.cs`** — ctor `:10` → `IReasoner`; `:17` → `reasoner.RunAsync(...)`. **Keep class name** (test node-id `"codex"` depends on it).
- **`Workflows/Executors/FormatRecipeExecutor.cs`** — ctor `:7` → `IReasoner`; `:33` → `reasoner.ReasonAsync(prompt, webSearch:false, ct, logLabel:"recipe import")` (search OFF).
- **`Workflows/VoiceMemoWorkflow.cs`** `:34`, **`Workflows/RecipeWorkflow.cs`** `:22` — `GetRequiredService<CodexRunner|ICodexRunner>()` → `GetRequiredService<IReasoner>()`.
- **`Orchestration/ErdaAgentResponder.cs`** — convert both `RunAsync` sites (`:28` gated session, `:53` throwaway session) to aggregated `RunStreamingAsync(...).ToAgentResponseAsync(ct)`, then reuse `ToReply` (`:57-75`) **unchanged** so `AgentReply` keeps tokens + `ToolsUsed`. Keep `_gate` + lazy `_session`.

### Erda.Core
- **`ServiceCollectionExtensions.cs`** — `:79-84` remove both Codex registrations; add `services.AddSingleton<IReasoner, ResponsesReasoner>()`. Drop the two Azure `[Required]` from `CredentialsOptions` validation (`:32-35`).
- **`Configuration/CredentialsOptions.cs`** — DELETE `AzureOpenAIEndpoint` (`:20-22`), `AzureOpenAIApiKey` (`:25-27`); KEEP `OpenAIApiKey` (transcription, `Transcriber.cs:36`).
- **`Configuration/ErdaOptions.cs`** — DELETE `ChatDeployment` (`:31-32`), `CodexModel`/`CodexReasoningEffort`/`CodexTimeout`/`CodexExecutable` (`:44-57`). ADD `ChatBaseUrl` `[Required]`, `ChatModel` `[Required]`, `ChatReasoningEffort` `[Required]`, optional `ChatApiKey` (default `"local"`, **not** `[Required]`). KEEP `VaultPath`/`DbPath`/`TranscribeModel`/`VoiceMemoSubfolder`.
- **`Configuration/BrowserOptions.cs`** — `:21-22` `Deployment` fallback repoints to `ChatModel`.
- **`Services/CodexRunner.cs` + `Services/ICodexRunner.cs`** — **DELETE**.
- **`Scheduling/ErrorWatch/CodexErrorAnalyzer.cs`** — ctor `:18` → `IReasoner`; `:24` → `reasoner.ReasonAsync(BuildPrompt(error), webSearch:false, ct)`. **Keep the `IErrorAnalyzer` seam name** (tests depend on it).
- **`Scheduling/Reminders/ReminderScheduler.cs`** — ctor `:23` → `IReasoner`; **remove the `if (r.DirectToCodex)` branch** (`:253-258`); those prompts run the agent path.
- **`Scheduling/Reminders/Reminder.cs`** `:34`, **`ReminderStore.cs`** — remove `DirectToCodex` flag/column plumbing + `directToCodex` params on `Append`/`Update`.
- **`WhatsApp/WhatsAppSender.cs`** — add `Task SetPresenceAsync(string chatJid, string state, CancellationToken ct = default)` to `IWhatsAppSender`; implement by cloning `SendAsync` (POST `{BridgeUrl}/presence`, body `{ to, state }`, same `X-Bridge-Secret`). **Best-effort** — swallow/Debug-log, never block the reply.
- **`WhatsApp/WhatsAppChannelService.cs`** — around the turn (`:121`) / reply send (`:129`): `SetPresenceAsync(replyTarget, "composing")` before `RespondAsync`, `"paused"` after in a `finally`. (Presence lives here, not the responder — this service holds `replyTarget` + `sender`.)

### Erda.Server
- **`Program.cs`** — `LogStartupConfig` (`:145-153`) arg list: drop `ChatDeployment`/`CodexModel`/`CodexReasoningEffort`, add `ChatBaseUrl`/`ChatModel`/`ChatReasoningEffort` (else won't compile).
- **`Api/Config/ConfigPanelService.cs`** — `:42-45` surfaced keys: swap codex/deployment → new chat keys (else won't compile).
- **Reminder DTOs/endpoints** — drop `DirectToCodex` from `CreateReminderRequest`/`UpdateReminderRequest`/`ReminderDto`.

### whatsapp-bridge (Go)
- **`send.go`** — register `mux.HandleFunc("/presence", presenceHandler(cfg, client))` (`:42`); add `presenceRequest{ To, State }`; `presenceHandler` mirrors `sendHandler` (method check + `secretEqual(X-Bridge-Secret)` + JSON decode + `types.ParseJID`), then map `"composing"→ChatPresenceComposing`, `"paused"→ChatPresencePaused`, call `client.SendChatPresence(ctx, jid, state, types.ChatPresenceMediaText)`.
- **`main.go`** — after connect, one-time `client.SendPresence(ctx, types.PresenceAvailable)`; guard `ErrNoPushName` on a brand-new session.

## 6. EF migration

```
dotnet ef migrations add DropReminderDirectToCodex --project Erda.Core --startup-project Erda.Server
```

Drops the `DirectToCodex` boolean column from the reminders table (entity property removed). Update the model snapshot.

## 7. Config & `.env`

- **Removed:** `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `Erda__ChatDeployment`, `Erda__CodexModel`, `Erda__CodexReasoningEffort`, `Erda__CodexTimeout`, `Erda__CodexExecutable`.
- **Kept:** `OPENAI_API_KEY` (transcription), `Erda__TranscribeModel`, `Erda__VaultPath`, `Erda__DbPath`, `Erda__VoiceMemoSubfolder`.
- **Added:** `Erda__ChatBaseUrl` (`http://127.0.0.1:10531/v1` for `make dev`), `Erda__ChatModel=gpt-5.5`, `Erda__ChatReasoningEffort=medium`, optional `Erda__ChatApiKey=local`.
- Update `.env.example` and the four startup-validation touch points (CredentialsOptions, ErdaOptions, `Program.cs` log, `ConfigPanelService`).

## 8. Tests

- **New fake:** `FakeReasoner : IReasoner` in `Erda.Tests/Fakes.cs` (replaces `FakeCodexRunner`), capturing `(Prompt, WebSearch, ReasoningEffort, LogLabel)`; implements both `ReasonAsync` + `RunAsync`. Hand-written `sealed`, no Moq (house style).
- **Delete:** `CodexRunnerTests.cs`, `ReasoningToolsTests.cs`.
- **Update:** `Fakes.cs` (`FakeAgentResponder.Reply` default `ToolsUsed` no longer `["consult_codex"]`); `ReminderSchedulerTests` (delete the Direct-to-Codex region, keep prescript/plain); `ReminderStoreTests` / `ReminderEndpointsTests` (drop `directToCodex`); `WorkflowCatalogTests` (`FakeReasoner` + `AddSingleton<IReasoner>`); `ConfigValidationTests` / `ConfigPanelServiceTests` / `BrowserOptionsTests` (new chat option names).
- **Leave alone (insulated by seams):** `ErrorWatchSchedulerTests` (keeps `IErrorAnalyzer`), `WhatsAppChannelServiceTests` / `UploadIntakeTests` (keep `IAgentResponder.RespondAsync` shape — streaming stays *internal* to the responder), `WebChatServiceTests` (the streaming exemplar).

## 9. Risks & open questions (with decisions)

1. **Docker reachability — DECIDED, follow-up.** `127.0.0.1:10531` is the container's own loopback, not the host proxy. `Erda__ChatBaseUrl` is a config key precisely so dev (`127.0.0.1`) and prod differ. Prod address (`host.docker.internal` + `extra_hosts`, or a sibling container) is a **deployment follow-up**, out of scope for this branch (dev-first experiment).
2. **`web_search` wire contract — LOW.** Proxy accepts literal `{"type":"web_search"}` (tested); SDK emits `OpenAI.Responses.WebSearchTool` (IL-confirmed). One live agent call early in implementation to confirm end-to-end.
3. **`OPENAI001` experimental — DECIDED.** Wrap the Responses construction in `#pragma warning disable OPENAI001`.
4. **`ChatApiKey` optionality — DECIDED.** Optional `Erda__ChatApiKey` defaulting to `"local"`; not `[Required]`.
5. **`delegate_vault_task` loss — ACCEPTED.** It relied on Codex running a real shell with the vault as cwd (no in-process equivalent). Removed with Codex; `ObsidianTools` (6 tools: read/write/search per note) remain as the partial replacement. Consistent with the approved scope.
6. **Keep interface/class names to minimize churn — DECIDED.** Preserve `IAgentResponder.RespondAsync` shape (streaming internal), `IErrorAnalyzer`, and the `CodexExecutor` class name.
7. **Streaming telemetry — DECIDED.** Aggregate via `ToAgentResponseAsync()` and reuse `ToReply` so `AgentReply` keeps token usage + `ToolsUsed`.
8. **Model is heavier.** Erda on `gpt-5.5` is slower than `gpt-5.4-mini` — intended (Erda *is* Codex now); the typing indicator covers the latency UX.

## 10. Out of scope

- Production deployment address for the proxy (follow-up).
- Removing the `~/.codex` bind mount / `codex` install from the Docker image (cleanup once the branch proves out).
- Any change to transcription (stays on `OPENAI_API_KEY`).
