# Jetson Docker + Komodo deployment — design

- **Date:** 2026-05-31
- **Status:** Approved (design); pending implementation plan
- **Scope:** Containerize and deploy the Erda stack (the .NET orchestrator + the Go
  WhatsApp bridge) onto an NVIDIA Jetson (ARM64 / aarch64) as an always-on Linux host,
  auto-deployed by Komodo on push.

## Context

Erda is a personal agent with two cooperating processes:

1. **Erda** (.NET 10, `Erda.csproj`) — the orchestrator. Calls Azure OpenAI (chat),
   the OpenAI platform (transcription), the `codex` CLI (subscription auth), reads/writes
   an Obsidian vault, exposes a WhatsApp inbound HTTP endpoint, and runs the error-watch
   scheduler.
2. **whatsapp-bridge** (Go, CGO-free) — owns the WhatsApp multi-device socket via
   `whatsmeow`; relays messages to/from Erda over local HTTP. No keys, no model logic.

The Jetson has a GPU, but **nothing here uses it** — every model call is cloud (Azure /
OpenAI / Codex subscription). The Jetson is simply the always-on ARM64 host. No
`nvidia-docker` runtime, no CUDA.

### The one worry that turned out to be a non-issue: Codex in a container

`codex` needs only two things, both of which travel into a container:

1. The `codex` **binary** — install the `aarch64` linux build into the Erda image.
2. The **logged-in session** — `~/.codex/auth.json`. Bind-mount the host's `~/.codex`
   into the container (RW, so token refresh persists). `CodexRunner` already strips
   `OPENAI_API_KEY` from the subprocess env (and inherits the rest, so `CODEX_HOME`
   flows through), forcing ChatGPT-subscription auth exactly as on the dev Mac.

## Decisions (locked)

| Decision | Choice |
|---|---|
| Hosting shape | **Full Docker + Komodo** (compose stack, auto-deploy on push) |
| Interaction surface | **WhatsApp-only (Production)** — DevUI not mapped, no published web port |
| Image build | **Built natively on the Jetson** (arm64) — no registry |
| Apple voice memos | Arrive **via WhatsApp audio** → downloaded to shared media dir → transcribed. No Mac-native input path needed. |
| Seq log server | **Point at existing `seq.phib.io`** (central hub) via env — no Seq container |

## Architecture

A single Compose stack, two services, one private internal network, **no published host
ports** (WhatsApp is the only surface):

```
┌──────────────────── erdanet (internal bridge network) ────────────────────┐
│   whatsapp-bridge (Go)  ──HTTP /channel/whatsapp/in──▶  erda (.NET 10)      │
│   owns WA socket        ◀──HTTP /send───────────────   orchestrator        │
└────────────────────────────────────────────────────────────────────────────┘
        │                                                  │
   bridge-data vol  (whatsmeow-session.db)          /vault   (host-synced Obsidian, RW)
   media vol  ◀────────── SHARED, same path ───────▶ /media   (WA downloads)
                                                     /codex   (host ~/.codex, RW)
                                                       │
                                       cloud: Azure OpenAI · OpenAI · Codex subscription · seq.phib.io
```

### Why no application code changes

Every coupling already routes through env / `appsettings.json`
(`WhatsApp:BridgeUrl`, `WhatsApp:MediaTempDir`, `Erda:VaultPath`, Seq settings, Kestrel
URL). The WhatsApp endpoint and all hosted workers are mapped **independent of
environment** (`Program.cs:89`, `app.MapWhatsAppChannel()` sits outside the
`IsDevelopment()` guard); only DevUI is gated. So Production runs the full WhatsApp +
error-watch + voice-memo stack with no DevUI. This is purely a packaging + config task.

## Services and images

### erda
- **Build:** multi-stage. `mcr.microsoft.com/dotnet/sdk:10.0` to `dotnet publish`,
  runtime `mcr.microsoft.com/dotnet/aspnet:10.0` (both multi-arch; arm64 pulled on the
  Jetson).
- **Codex CLI:** in the runtime stage, fetch the `codex` `aarch64-unknown-linux` release
  binary, place on `PATH`, make executable. Set `CODEX_HOME=/codex`.
- **Process:** `dotnet Erda.dll`, Kestrel on `0.0.0.0:5167` (`ASPNETCORE_URLS`).

### whatsapp-bridge
- **Build:** multi-stage `golang` → static binary (CGO-free) → distroless/scratch runtime.
- **Process:** the bridge binary; binds `0.0.0.0:8088` (inside the container only — the
  port is **not** published, so the README's "localhost only" intent is preserved by the
  unpublished private network).

## Networking

| From | To | URL |
|---|---|---|
| bridge → erda (inbound) | erda:5167 | `ERDA_INBOUND_URL=http://erda:5167/channel/whatsapp/in` |
| erda → bridge (send) | bridge:8088 | `WhatsApp__BridgeUrl=http://whatsapp-bridge:8088` |

Service-name DNS on `erdanet`. The only change from the dev setup is `127.0.0.1` →
service names, and the bridge binding `0.0.0.0` instead of `127.0.0.1` (required so the
peer container can reach it; still unpublished to the host).

## Volumes and state

| Mount | In container(s) | Source | Mode | Purpose |
|---|---|---|---|---|
| `/codex` | erda | host `~/.codex` (bind) | RW | Codex subscription session; refresh writes back |
| `/vault` | erda | host synced Obsidian dir (bind) | RW | vault read/write |
| `/media` | **erda + bridge** | named volume `media` | RW | **shared**; bridge writes downloaded media, hands erda the absolute path, erda opens/transcribes/deletes it (`WhatsAppChannelService.cs:91-105`). Both must see the same path. |
| `/data` | bridge | named volume `bridge-data` | RW | `whatsmeow-session.db` — on its own volume so rebuilds never drop the WhatsApp link |

## Configuration / environment matrix

### erda
```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:5167
CODEX_HOME=/codex
Erda__VaultPath=/vault
WhatsApp__Enabled=true
WhatsApp__BridgeUrl=http://whatsapp-bridge:8088
WhatsApp__MediaTempDir=/media
WhatsApp__SharedSecret=<secret>
WhatsApp__OwnerNumber=<+E164>
Seq__ServerUrl=https://seq.phib.io
Seq__ApiKey=<seq api key>
Seq__IngestToErda=true
ErrorWatch__Enabled=true
AZURE_OPENAI_ENDPOINT=<...>
AZURE_OPENAI_API_KEY=<...>
OPENAI_API_KEY=<...>            # transcription only; stripped from the Codex subprocess
```

### whatsapp-bridge
```
BRIDGE_LISTEN=0.0.0.0:8088
ERDA_INBOUND_URL=http://erda:5167/channel/whatsapp/in
SHARED_SECRET=<same secret as erda>
OWNER_JID=<owner>@s.whatsapp.net
SESSION_DB=/data/whatsmeow-session.db
MEDIA_DIR=/media
```

### Secrets
Provided through Komodo's env/secret management and injected as container env: the Azure
endpoint + key, `OPENAI_API_KEY`, `SHARED_SECRET` (identical on both services),
`OWNER_JID` / `OwnerNumber`, and `Seq__ApiKey`. The Codex credential is **not** an env
var — it's the mounted `~/.codex` directory.

## Seq

The error-watch scheduler ships inside the erda container. It points at the existing
central Seq at `https://seq.phib.io` (set via `Seq__ServerUrl` + `Seq__ApiKey`). Erda also
ships its own logs there (`Seq__IngestToErda=true`). No Seq container in this stack.

## One-time bootstrap on the Jetson (interactive, once each)

1. **Codex login:** `codex login` on the host (device-code flow; prints a URL to open on
   another machine over SSH) → populates `~/.codex`. Rarely needs repeating.
2. **WhatsApp QR:** first bridge start needs the QR scanned. Run
   `docker compose run --rm whatsapp-bridge` once, scan via *WhatsApp → Linked devices →
   Link a device*; the session persists in the `bridge-data` volume; subsequent detached
   starts connect silently.

## Komodo deployment

- A Komodo **Stack** resource pointed at this repo.
- Webhook on push → `docker compose up -d --build` (build happens on the Jetson).
- `restart: unless-stopped` on both services ⇒ survives host reboots.

## Deliverables (produced by the implementation plan)

- `Dockerfile` (Erda, multi-stage + Codex aarch64 binary) and `.dockerignore`
- `whatsapp-bridge/Dockerfile` (multi-stage static → distroless)
- `docker-compose.yml` (the two-service stack, network, volumes)
- a stack-level `.env.example` documenting every variable above
- README "Deploy on a Jetson (Docker + Komodo)" section, incl. the one-time bootstrap
- **No application code changes.**

## Out of scope / non-goals

- GPU / CUDA usage (nothing here needs it).
- DevUI exposure in production (reachable only via SSH tunnel during development).
- Running Seq locally (using the existing `seq.phib.io`).
- Obsidian **sync** itself: a sync client (Syncthing / obsidian-git / etc.) must run on
  the Jetson host, writing into the directory mounted as `/vault`. The container only
  reads/writes files; it does not sync them. (Setting that up is a separate task.)
- Multi-arch / registry builds (building natively on the Jetson).

## Open items to verify during implementation

1. **`CODEX_HOME` honored by the installed `codex` CLI version** — confirm the CLI reads
   `CODEX_HOME=/codex`; fallback is to mount `~/.codex` at the container user's real
   `$HOME/.codex`.
2. **Codex aarch64 release artifact name + install method** — pin the exact release asset
   / version fetched in the Dockerfile.
3. **Codex sandbox inside a container** — `consult_codex` runs `codex exec` with a
   read-only sandbox + web search; verify the sandbox mechanism works inside the
   container (no extra kernel capabilities needed) on the Jetson kernel.
4. **dotnet runtime image package needs** — the aspnet runtime image may need
   `ca-certificates`/`curl` (or a download stage) to fetch + run Codex and to trust TLS
   for Azure/OpenAI/Seq.
5. **Vault file ownership/permissions** between the host sync client and the container
   user (uid/gid) so writes from Erda are visible to sync and vice versa.
