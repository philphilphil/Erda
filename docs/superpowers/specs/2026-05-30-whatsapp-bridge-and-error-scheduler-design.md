# Design: WhatsApp access to Erda + a Seq error-watch scheduler

**Date:** 2026-05-30
**Branch:** `feature/whatsapp-bridge`
**Status:** approved (brainstormed with Phil; implement-overnight authorized)

## Goal

Two related capabilities:

1. **Reach Erda from my phone over WhatsApp.** Send text / voice notes / photos to a
   dedicated bot number; Erda runs its full agent+tools loop and replies. Erda can also
   message me unprompted (proactive pings).
2. **Error-watch scheduler.** On an interval, poll a (remote) Seq instance for new
   `Error`/`Fatal` events, hand each new error to Codex for analysis, and push the analysis
   to me over WhatsApp.

## Decisions (locked during brainstorming)

- **Channel:** WhatsApp.
- **Engine:** **whatsmeow** (Go) — the mautrix-whatsapp engine; most battle-tested unofficial
  lib, single static binary, SQLite session, auto-reconnect. (Baileys was the alternative; we
  picked whatsmeow for reliability of an always-on bridge.)
- **Integration style:** **thin dumb relay + a WhatsApp channel module inside Erda.** The Go
  sidecar holds the WhatsApp socket and nothing else — no API keys, no model logic. All AI and
  every credential stay in Erda. This keeps Erda the single orchestrator and preserves the
  three-credential model.
- **Identity:** a dedicated bot number, linked once by QR (done by Phil in the morning).
- **Owner-only:** Erda answers only the whitelisted owner number; the bridge also drops
  non-owner senders and groups (defense in depth).
- **Seq:** remote, queried over its HTTP API; URL + API key come from config. Overnight we
  validate the pipeline against a throwaway local Seq (docker-compose) as a stand-in.
- **Scheduler default:** poll every **15 min**, watch **Error+Fatal**, **dedup by signature**,
  send **one WhatsApp per new error-group** with Codex's analysis. Cadence is a one-line config
  change (5-min / hourly-digest variants noted).

## Architecture

```
 ┌─────────────────────┐
 │  Phone (WhatsApp)   │
 └─────────┬───────────┘
           │  WhatsApp multi-device protocol
           ▼
 ┌──────────────────────────────────────────────┐
 │  whatsapp-bridge/  (Go, whatsmeow — 1 binary) │
 │  • WA socket + session in SQLite, QR once      │
 │  • DUMB RELAY: owner-JID only, no API keys     │
 │  inbound:  msg → download media to temp dir →  │
 │            POST  Erda /channel/whatsapp/in     │
 │  outbound: POST /send  ◄── Erda calls this     │
 └─────────┬───────────────────────▲────────────┘
           │ 127.0.0.1 + X-Bridge-Secret (both hops)
           ▼                        │
 ┌──────────────────────────────────┴───────────┐
 │  Erda (ASP.NET Core orchestrator)             │
 │  WhatsAppChannel (new):                        │
 │    whitelist · per-sender thread · dispatch    │
 │      text  → erda agent loop                   │
 │      voice → Transcriber(ogg) → agent          │
 │      image → ChatMessage(image part) → agent   │
 │    WhatsAppSender → bridge /send               │
 │  ErrorWatchScheduler (new, BackgroundService): │
 │    Seq query → dedup → Codex → WhatsAppSender   │
 │  Serilog → Seq (Erda's own errors land there)  │
 │  REUSE: erda agent · Obsidian tools ·          │
 │         consult_codex · voice-memo workflow ·   │
 │         Transcriber · CodexRunner               │
 └────────────────────────────────────────────────┘
```

Two OS processes on the Mac: **Erda** (`:5167`) and the **bridge** (`:8088`, localhost only).

## HTTP contracts (the bridge ⇄ Erda seam)

Both hops are bound to `127.0.0.1` and require a shared secret header
`X-Bridge-Secret: <secret>`. A mismatch returns `401`.

### Inbound — bridge → Erda

`POST http://127.0.0.1:5167/channel/whatsapp/in`

```jsonc
{
  "from":      "4915123456789@s.whatsapp.net",  // sender JID
  "chat":      "4915123456789@s.whatsapp.net",  // reply target JID
  "type":      "text" | "audio" | "image",
  "text":      "hello",                          // text body, or image caption ("" if none)
  "mediaPath": "/tmp/erda-bridge/ab12.ogg",      // absolute, same-host; audio/image only
  "mimeType":  "audio/ogg; codecs=opus",         // media only
  "messageId": "3EB0...",
  "timestamp":  1748600000
}
```

Erda replies **`202 Accepted` immediately** and processes asynchronously; the reply (and any
proactive ping) is delivered out-of-band through the bridge's `/send`. This avoids long-held
connections while the agent runs (consult_codex / Codex can take 10–30 s).

### Outbound — Erda → bridge

`POST http://127.0.0.1:8088/send`

```jsonc
{ "to": "4915123456789@s.whatsapp.net", "text": "reply text" }
```

Returns `200 OK` on success.

### JID normalization

Owner is configured as a plain international number (`+49 151 2345 6789`); Erda normalizes to
`4915123456789@s.whatsapp.net` (strip `+`, spaces, leading zeros on the country trunk). The
whitelist compares on the bare user part so device suffixes (`:12@...`) don't matter.

## Per-type behavior

- **Text** → straight into the `erda` agent loop; reply sent back.
- **Voice note** → `Transcriber.TranscribeAsync` on the WhatsApp `.ogg/opus` (OpenAI STT accepts
  ogg — verify at build), transcript becomes the user message, agent answers. "Save this as a
  note" is handled by the agent via existing write tools / `process_voice_memo` on request.
- **Image** → passed to gpt-5-mini as an image content part with the caption as the prompt
  (vision — verify the deployment is vision-enabled and MAF passes image content through the
  Azure client). E.g. "turn this whiteboard into a note" → vision + existing write tool.

## Conversation continuity

v1 keeps **one in-memory `AgentThread`** for the owner so context carries across messages;
it resets on Erda restart. Persistence is a later add (out of scope).

## Proactive pings

- `WhatsAppSender` is the single outbound path (used by replies, scheduler alerts, and any
  proactive message).
- A `message_me` **AITool** lets the agent send me a WhatsApp message during a turn.
- v1 ships the **plumbing + the tool**. Actual *triggers* (reminders, schedules) beyond the
  error scheduler are a separate future feature — no general scheduler/reminders engine now.

## Error-watch scheduler

`ErrorWatchScheduler : BackgroundService` (registered as a hosted service, gated on
`ErrorWatch:Enabled`).

Loop (PeriodicTimer, default 15 min):

1. **Query Seq** via `SeqClient` for events at/above `MinLevel` (default `Error`) newer than the
   stored watermark. Seq HTTP API: `GET /api/events?...` with `X-Seq-ApiKey` header; page
   forward with `afterId` (robust monotonic watermark) — **verify exact endpoint/params via
   Context7/web at build time**. Optional `Filter` (Seq filter expression) to scope by app/source.
2. **Dedup** new events by **signature** = `Level | MessageTemplate | ExceptionType`. Keep a
   bounded set of seen signatures in the state file so a recurring error doesn't re-alert every
   poll. (Recurrences within the retention window are suppressed; count could be surfaced later.)
3. For each **new** signature, build a compact error context (level, message, exception, key
   properties, count, first/last seen) and call **`CodexRunner.RunPromptAsync`** (no web search
   needed by default; subscription auth, no API key) with an "analyze this production error"
   instruction → returns Markdown analysis (likely cause + suggested fix).
4. **`WhatsAppSender.SendAsync(ownerJid, alertText)`** where `alertText` = a short header (level,
   message, count) + Codex's analysis, trimmed to a sane WhatsApp length.
5. **Advance the watermark** and persist `{ lastEventId, seenSignatures[] }` to a JSON state
   file (configurable path, default under the app data dir). On first run with no state, start
   from "now" (don't replay history).

Failure handling: a Seq/Codex/bridge error is logged and the loop continues next tick; the
watermark only advances past events that were processed.

## Configuration

`appsettings.json` additions (secrets/number via env or user-secrets, placeholders committed):

```jsonc
"WhatsApp": {
  "Enabled": true,
  "OwnerNumber": "+490000000000",          // FILL ME (Phil's main number)
  "BridgeUrl": "http://127.0.0.1:8088",
  "SharedSecret": "change-me-dev-secret",   // shared with the bridge
  "MediaTempDir": "/tmp/erda-bridge"
},
"ErrorWatch": {
  "Enabled": true,
  "PollInterval": "00:15:00",
  "MinLevel": "Error",
  "Filter": null,                            // optional Seq filter expression
  "StateFile": null                          // default: <AppData>/erda/errorwatch-state.json
},
"Seq": {
  "ServerUrl": "https://seq.example.com",    // FILL ME (remote Seq)
  "ApiKey": "",                              // FILL ME (read/query key); also used for ingestion if set
  "IngestToErda": true                       // ship Erda's own logs to Seq via Serilog
}
```

Bridge env (`whatsapp-bridge/.env.example`):

```
ERDA_INBOUND_URL=http://127.0.0.1:5167/channel/whatsapp/in
BRIDGE_LISTEN=127.0.0.1:8088
SHARED_SECRET=change-me-dev-secret
OWNER_JID=4915123456789@s.whatsapp.net     # only relay this sender
SESSION_DB=./whatsmeow-session.db
MEDIA_DIR=/tmp/erda-bridge
```

## Security

- Both HTTP hops: `127.0.0.1` only + `X-Bridge-Secret`. Nothing else on the box can inject
  messages or trigger sends.
- Erda whitelists the owner; the bridge independently drops non-owner senders and group chats.
- The bridge never holds an OpenAI/Azure key. Codex stays on the ChatGPT subscription.
- Media written to a dedicated temp dir; cleaned best-effort after processing.

## Component inventory (new files)

Erda (.NET):
- `Configuration/WhatsAppOptions.cs`, `Configuration/ErrorWatchOptions.cs`, `Configuration/SeqOptions.cs`
- `Services/WhatsAppSender.cs` — outbound HTTP client to the bridge
- `Channels/WhatsAppChannelService.cs` — whitelist, thread, dispatch
- `Channels/WhatsAppInbound.cs` — minimal-API endpoint mapping + request DTO
- `Tools/NotifyTools.cs` — `message_me` AITool
- `Services/Seq/SeqClient.cs` — Seq query client + event DTO
- `Services/Seq/SeqEvent.cs`
- `Scheduling/ErrorWatchScheduler.cs` — the BackgroundService
- `Scheduling/ErrorWatchState.cs` — watermark + seen-signatures persistence
- `Scheduling/ErrorSignature.cs` — signature + dedup logic (pure, unit-tested)
- Edits: `Program.cs` (DI + endpoint + hosted service + Serilog), `appsettings.json`, `Erda.csproj`

Bridge (Go) — `whatsapp-bridge/`:
- `go.mod`, `main.go` (connect/QR/session/reconnect, inbound relay, `/send` server), `.env.example`, `README.md`

Infra/tests:
- `docker-compose.seq.yml` — local Seq stand-in
- `Erda.Tests/` — xUnit project (pure-logic unit tests)
- `MORNING.md` — wire-up checklist

## Phasing (all delivered; sequenced so each is independently testable)

- **P1** — bridge skeleton + text chat + owner-only + shared secret + `/send` + `message_me`.
- **P2** — voice notes (Transcriber on ogg).
- **P3** — images (vision content part).
- **P4** — Serilog→Seq + SeqClient + ErrorWatchScheduler + Codex analysis + alert.

## Test strategy

- **Pure unit tests (xUnit):** JID normalization + whitelist; inbound payload (de)serialization;
  dispatch routing (type → branch) with a fake agent + fake sender; error signature + dedup;
  watermark advance/persist round-trip; Seq query-URL builder; alert formatting; `WhatsAppSender`
  sets the secret header and posts the right body (HttpMessageHandler stub).
- **Live overnight (no keys/phone needed):** Codex analysis path end-to-end via `CodexRunner`
  (ChatGPT subscription). Stand up local Seq, ingest a synthetic error, run one scheduler tick,
  assert it queries → dedups → calls Codex → posts to a stub `/send`.
- **Deferred to morning (need phone/keys):** real QR link, live WhatsApp round-trip, chat agent
  (Azure key), transcription (OpenAI key), image vision.

## Build-time verifications (do not assume)

1. whatsmeow current API: connect, QR channel, event handler, media download, `SendMessage`.
2. Seq HTTP query API: exact path/params for "events ≥ level since watermark" + API-key header.
3. gpt-5-mini deployment is vision-capable and MAF passes an image content part through the
   Azure OpenAI chat client.
4. OpenAI `gpt-4o-transcribe` accepts `.ogg/opus` directly (else transcode via ffmpeg in bridge).
5. Cleanest way to call the `erda` `AIAgent` in-process with a persistent `AgentThread`
   (resolve the registered agent vs. `ErdaAgent.Create`), and to register a `BackgroundService`
   alongside the MAF host.
6. Serilog + `Serilog.Sinks.Seq` wiring on .NET 10 / ASP.NET Core host.

## Out of scope (YAGNI)

Multi-user, group chats, persisted chat history, reminders/cron beyond the error scheduler, a
web/PWA surface, read receipts/typing indicators, message editing, outbound media.
