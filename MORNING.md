# Morning checklist — WhatsApp access + error-watch scheduler

Branch: **`feature/whatsapp-bridge`**. Everything below was built and offline-tested overnight;
the steps here are the parts that need your phone + live keys.

## What got built

- **WhatsApp channel in Erda** — text, voice notes (transcribed), and images (vision); owner-only;
  plus a `message_me` tool so Erda can ping you proactively.
- **Go whatsmeow bridge** (`whatsapp-bridge/`) — a dumb relay holding the WhatsApp socket. Builds
  CGO-free, `go vet` clean.
- **Error-watch scheduler** — every 15 min, queries your Seq for new `Error`/`Fatal` events, has
  Codex analyze each new one, and WhatsApps you the analysis. Serilog also ships Erda's own logs to
  Seq.
- **Tests** — 40 unit tests + 1 live Seq integration check, all green; both projects build clean.
  The live Seq query+mapping path was verified end-to-end against a real Seq overnight.

## 1. Restore your API keys

You invalidated them. Re-issue and export in the shell that runs Erda (Codex needs nothing — it's
on your ChatGPT subscription):

```bash
export AZURE_OPENAI_ENDPOINT="https://<resource>.openai.azure.com/"
export AZURE_OPENAI_API_KEY="<foundry-key>"
export OPENAI_API_KEY="sk-...<platform-key>..."
```

## 2. Fill in the new config (keep secrets out of git — use env vars)

Erda reads top-level `WhatsApp`, `Seq`, `ErrorWatch` sections (placeholders are in
`appsettings.json`). Override with env vars (`__` nests sections):

```bash
export WhatsApp__OwnerNumber="+49…"                       # your MAIN number (you message FROM it, alerts go TO it)
export WhatsApp__SharedSecret="$(openssl rand -hex 24)"   # remember this — the bridge must match
export Seq__ServerUrl="https://<your-remote-seq>"
export Seq__ApiKey="<read/query API key>"
```

Defaults already sensible: poll 15 min · `Error`+`Fatal` · dedup by signature · Codex analysis ·
max 5 alerts/poll. To test fast later: `export ErrorWatch__PollInterval=00:01:00`.

## 3. Configure + build the bridge (one-time)

```bash
cd whatsapp-bridge
cp .env.example .env        # then edit .env:
#   SHARED_SECRET=<the same value as WhatsApp__SharedSecret above>
#   OWNER_JID=<your main number's digits>@s.whatsapp.net   (e.g. 49151…@s.whatsapp.net)
#   (the other defaults already match Erda)
go build ./...
```

## 4. Start both processes

**Terminal A — Erda** (bind localhost; keep it there so only the bridge can reach the channel):

```bash
cd /Users/phil/Projects/Erda
ASPNETCORE_URLS=http://127.0.0.1:5167 dotnet run
```

**Terminal B — the bridge** (first run prints a QR):

```bash
cd whatsapp-bridge
go run .
```

Scan the QR with the **dedicated bot number's** phone (WhatsApp → Linked devices → Link a device).
The session is saved to `whatsmeow-session.db`, so you only scan once.

## 5. Try it from your phone

- Message the **bot number** "hi" from your main number → Erda replies.
- Send a **voice note** → transcribed, then answered. ("Save that as a note" routes to the vault.)
- Send a **photo + caption** ("turn this into a note") → vision. *Needs the gpt-5-mini Foundry
  deployment to be vision-enabled; if not, it'll error clearly — tell me and I'll add a vision path.*
- "**message me** a test reminder in this chat" → exercises the proactive `message_me` tool.
- **Scheduler:** cause an `Error` in something that logs to your Seq (or just wait). Within the poll
  interval you get a WhatsApp with Codex's take. Lower `ErrorWatch__PollInterval` to test quickly.

## Optional: a local Seq stand-in (no remote needed)

```bash
docker compose -f docker-compose.seq.yml up -d        # anonymous, http://localhost:5341
export Seq__ServerUrl=http://localhost:5341 Seq__ApiKey=
# ingest a test error:
curl -X POST http://localhost:5341/ingest/clef -H 'Content-Type: application/vnd.serilog.clef' \
  --data-binary '{"@t":"'"$(date -u +%Y-%m-%dT%H:%M:%S.000Z)"'","@l":"Error","@mt":"Test {N}","N":1,"Application":"Erda","@x":"System.Exception: boom"}'
docker compose -f docker-compose.seq.yml down -v       # when done
```

## Gotchas found & handled overnight

- **Seq first-run auth:** recent Seq refuses to start without an admin password or an explicit
  no-auth opt-out. The compose file sets `SEQ_FIRSTRUN_NOAUTHENTICATION` for the *local* stand-in;
  your *remote* Seq keeps auth and uses `Seq:ApiKey`.
- **Namespacing:** `Erda.Services.Seq` shadowed the `Seq.Api` package — `SeqClient` uses
  `global::Seq.Api` to disambiguate.
- **MAF 1.8:** the agent is a keyed singleton (`GetRequiredKeyedService<AIAgent>("erda")`), conversation
  state is an `AgentSession` from `agent.CreateSessionAsync()` (not `new`), runs return
  `AgentRunResponse.Text`.

## Still unverified (needs your phone / live keys — that's the point of this checklist)

Live WhatsApp round-trip (QR), the chat agent (Azure key), transcription (OpenAI key on a real ogg),
and image vision. Everything offline (routing, whitelist, dedup, Seq query+mapping, alert
formatting, sender) is tested and green.
