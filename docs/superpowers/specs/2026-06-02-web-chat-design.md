# Web chat for Erda — design

**Date:** 2026-06-02
**Status:** Implemented (+ follow-up below)
**Branch:** erda-panel-vue-migration

> **Follow-up (2026-06-02): browser-persisted history + session-id liveness.**
> The original v1 was fully ephemeral, so switching sidebar sections (the view
> unmounts) cleared the visible chat. Added:
> - **Server:** `WebChatService` mints a `SessionId` (GUID) per agent session —
>   on first message, re-minted after `Reset()`, `null` when no session exists.
>   The terminating SSE frame now carries it (`{"done":true,"sessionId":"…"}`),
>   and `GET /api/chat/session` returns the live id.
> - **Client:** chat state moved into a shared `useChat()` composable persisted to
>   `localStorage` (survives section-switching *and* refresh). On load it calls
>   `GET /api/chat/session` and compares the live id to the stored one; if the
>   server has no session or a different id, it shows an amber "Erda restarted —
>   it no longer remembers the messages above" banner (history kept, marked
>   stale; cleared on the next turn). This keeps the *display* persistent while
>   staying honest about when the agent's in-memory context is gone.
>
> Still out of scope: actually *restoring* the agent's memory across restarts
> (MAF can serialize a session, but that means persisting conversation state to
> disk/DB — a much larger change than display-persistence).

## Goal

Add a chat interface to the existing Vue control-panel SPA, as an alternative to
WhatsApp for testing the agent and for everyday use on the PC. Talk to the same
`erda` agent — its tools, prompt, reminders — over the browser instead of the phone.

## Decisions (from brainstorming)

| Question | Decision | Why |
|---|---|---|
| Conversation memory | **Separate thread**, isolated from WhatsApp | Test freely on the PC without polluting the real conversation on the phone |
| Reply style | **Token-by-token streaming** | MAF 1.8.0 supports it natively (`RunStreamingAsync`) and the SSE plumbing already exists; feels live during slow Codex/tool calls |
| Input types | **Text only** (v1) | Covers nearly all testing/PC use; voice/image are arguably redundant on a PC and can be added later |
| Persistence | **Ephemeral** | It's a test/PC tool; no DB persistence in v1 |

## Architecture

One real fork: where the chat's agent logic lives.

**Chosen:** a dedicated `WebChatService` in `Erda.Agents`, owning its own
`AgentSession` and exposing streaming. **Not** an extension of Core's
`IAgentResponder`.

Reasoning: `IAgentResponder` (in `Erda.Core/Abstractions/`) is deliberately
non-streaming and MAF-free so Core has no dependency on the Agent Framework.
Web chat is inherently a MAF + Server concern. `Erda.Server` already references
`Erda.Agents`, so the endpoint can inject the service directly. A separate
service leaves the WhatsApp dispatch path (`WhatsAppChannelService` →
`ErdaAgentResponder`) completely untouched and gives us the isolated thread for free.

```
Browser (ChatView.vue)
  │  POST /api/chat { text }      (X-Requested-With: erda-panel)
  ▼
Erda.Server/Api/Chat/ChatEndpoints.cs   ── text/event-stream ──▶ deltas back to browser
  │  IWebChat.StreamReplyAsync(text)
  ▼
Erda.Agents/WebChat/WebChatService.cs
  │  agent.RunStreamingAsync(turn, _session)   ← own AgentSession, separate from WhatsApp's
  ▼
keyed AIAgent "erda"  (same agent, tools, prompt as WhatsApp)
```

## Components

### Backend

**`Erda.Agents/WebChat/IWebChat.cs`** (new — seam for testability)
- `IAsyncEnumerable<string> StreamReplyAsync(string text, CancellationToken ct)`
- `void Reset()`

**`Erda.Agents/WebChat/WebChatService.cs`** (new — implements `IWebChat`)
- Injects the keyed `AIAgent` (`ErdaAgent.Name` = `"erda"`), `CurrentTimeContext`,
  `IActivityRecorder`.
- Holds its **own** `AgentSession? _session` + a `SemaphoreSlim _gate` — entirely
  separate from `ErdaAgentResponder`'s. Concurrent WhatsApp + web turns are
  independent HTTP calls against a stateless `AIAgent`; no shared locking needed.
- `StreamReplyAsync`:
  1. Acquire `_gate` (serialize within the web channel).
  2. Lazily create `_session` if null.
  3. Build the turn: `[ timeContext.Message(), new ChatMessage(ChatRole.User, text) ]`
     — the time-context prepend matches WhatsApp so relative scheduling
     ("remind me in 5 min") works.
  4. `await foreach (var update in agent.RunStreamingAsync(turn, _session, ct))`
     → `yield return` each text delta (from `update.Contents` `TextContent`).
     Accumulate full text + usage as you go.
  5. In `finally`: release `_gate`, and record one `agent_run` activity entry
     (summary = first ~100 chars of the reply) so web turns appear in the Activity
     feed, consistent with WhatsApp.
- `Reset()`: drop `_session` (the `/clear` equivalent for the web thread).

**`Erda.Server/Api/Chat/ChatEndpoints.cs`** (new — follows the `MapXxxEndpoints` pattern)
- `MapChatEndpoints(this RouteGroupBuilder group)`:
  - `POST /api/chat` `{ text }` → `text/event-stream`. Set headers
    (`Content-Type: text/event-stream`, `Cache-Control: no-cache`,
    `X-Accel-Buffering: no`), then per delta write `data: {"delta":"…"}\n\n` and
    flush; finish with `data: {"done":true}\n\n`; on exception write
    `data: {"error":"…"}\n\n`. Same flush-per-event approach as
    `ActivityEndpoints.StreamActivityAsync`.
  - `POST /api/chat/reset` → `IWebChat.Reset()`, `Results.Ok()`.
- DTO: `ChatRequest(string Text)` (request). Stream payloads are tiny inline JSON
  objects, serialized like the activity stream does.

**Wiring**
- `Erda.Agents/ServiceCollectionExtensions.cs` (`AddErdaAgents`): register
  `services.AddSingleton<IWebChat, WebChatService>();`
- `Erda.Server/Api/PanelApi.cs`: add `data.MapChatEndpoints();` to the
  **auth-required + CSRF** group (same protection as reminders/prompt/config).

### Frontend

**`web/src/views/ChatView.vue`** (new)
- State: `messages: { role: 'user' | 'assistant'; text: string }[]`, `streaming: boolean`.
- Message list with user/assistant bubbles; input box; send on Enter (Shift+Enter
  newline); a "thinking…" indicator on the pending assistant bubble until the first
  delta; input disabled while `streaming`.
- "New chat" button → `resetChat()` + clear local `messages`.
- Follows the existing design system (`Card`, `Icon`, CSS tokens, `page` /
  `page-header` layout conventions).

**`web/src/api/client.ts`** (edit)
- `streamChat(text, onDelta, onDone, onError)` — `fetch('/api/chat', { method: 'POST',
  credentials: 'include', headers: { 'X-Requested-With': 'erda-panel',
  'Content-Type': 'application/json' }, body })`, then read `res.body` via a
  `ReadableStream` reader, buffer by `\n\n`, parse each `data:` line, dispatch deltas.
  (Not `EventSource` — that's GET-only; chat POSTs the message.)
- `resetChat()` — `post('/api/chat/reset')`.

**`web/src/router.ts`** (edit) — add `{ path: '/chat', component: ChatView }`.

**`web/src/components/Sidebar.vue`** (edit) — add a `/chat` nav item under "Operate".

**`web/src/components/Icon.vue`** (edit) — add a `chat` (speech-bubble) icon path.

## Behavior / scope (v1, YAGNI)

- Text only. Streaming replies. Thread isolated from WhatsApp.
- **Ephemeral**: the server-side `AgentSession` persists until `Reset()` or app
  restart; on a page reload the UI starts empty while the server still holds
  context. No DB persistence, no history-load endpoint. Explicit "New chat" resets.
- No changes to the WhatsApp channel, the scheduler, or Core's `IAgentResponder`.
- No new NuGet/npm packages, no EF migrations.

## Error handling

- Agent/stream exceptions → `data: {"error":"…"}` event; the view renders the
  assistant bubble as an error and re-enables input.
- Client disconnect (navigates away mid-stream) → `OperationCanceledException`
  caught in the endpoint, same as the activity stream; `CancellationToken` flows
  into `RunStreamingAsync` so the agent run is cancelled.
- Auth: behind the panel's cookie auth (when `Panel:Password` is set). A 401 is
  handled by the SPA's existing `onUnauthorized` hook (redirect to `/login`).

## Testing

- **`IWebChat`** seam lets `ChatEndpoints` tests fake the stream and assert SSE
  framing (`data: {"delta":…}` … `data: {"done":true}`), error events, and the
  CSRF/auth requirement.
- **`WebChatService`** is tested by wrapping a fake `IChatClient` (returning canned
  streaming updates) as an `AIAgent` via `.AsAIAgent(...)`, then asserting:
  - deltas are yielded in order and concatenate to the full reply,
  - `_session` is reused across two turns (second turn sees first turn's context),
  - `Reset()` starts a fresh session,
  - exactly one `agent_run` activity entry is recorded per turn.
- Tests live in `Erda.Tests/` (xUnit), run via
  `dotnet test Erda.Tests/Erda.Tests.csproj`.

## Out of scope (possible follow-ups)

- Image/voice input (WhatsApp parity).
- Persisted chat history across reloads/restarts.
- A "continue the WhatsApp conversation" toggle (the shared-thread option).
- Showing per-turn token/tool telemetry inline in the chat UI (it's in Activity).
