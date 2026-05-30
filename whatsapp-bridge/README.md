# whatsapp-bridge

A small, dumb relay between **WhatsApp** and the **Erda** personal agent.

It owns the WhatsApp multi-device socket and session (via the unofficial
[`whatsmeow`](https://pkg.go.dev/go.mau.fi/whatsmeow) library — the same engine
as mautrix-whatsapp) and bridges messages over local HTTP. It contains **no API
keys** and **no model logic**:

- **Inbound** (WhatsApp → Erda): each accepted message is POSTed as JSON to
  `ERDA_INBOUND_URL`.
- **Outbound** (Erda → WhatsApp): an HTTP server on `BRIDGE_LISTEN` exposes
  `POST /send` to push a text message back to a WhatsApp JID.

The binary is **pure Go** (CGO-free): it uses the `modernc.org/sqlite` driver,
so `go build ./...` works without a C toolchain.

## Configuration

All configuration comes from environment variables. A local `.env` file is
loaded automatically if present (see `.env.example`); otherwise export the
variables in your shell.

| Variable           | Required | Default                 | Description                                                                 |
| ------------------ | :------: | ----------------------- | --------------------------------------------------------------------------- |
| `ERDA_INBOUND_URL` |   yes    | —                       | Where inbound messages are POSTed, e.g. `http://127.0.0.1:5167/channel/whatsapp/in`. |
| `BRIDGE_LISTEN`    |    no    | `127.0.0.1:8088`        | host:port for the outbound HTTP server. Bind localhost only.                |
| `SHARED_SECRET`    |   yes    | —                       | Sent on inbound POSTs and required on `/send`, as header `X-Bridge-Secret`. |
| `OWNER_JID`        |   yes    | —                       | Only messages from this sender are relayed; everything else is dropped.     |
| `SESSION_DB`       |    no    | `./whatsmeow-session.db`| Path to the whatsmeow SQLite session store.                                 |
| `MEDIA_DIR`        |    no    | `/tmp/erda-bridge`      | Directory for downloaded media (created if missing).                        |

```bash
cp .env.example .env
# edit .env, then:
go run .
```

## Running and the first-run QR flow

```bash
go run .
```

- **First run (no session yet):** the bridge prints a **QR code** to the
  terminal and logs `scan this with WhatsApp > Linked devices > Link a device`.
  On your phone, open **WhatsApp → Settings → Linked devices → Link a device**
  and scan it. The session is then saved to `SESSION_DB`.
- **Subsequent runs:** it connects **silently** using the saved session — no QR.

Press `Ctrl+C` (SIGINT) or send SIGTERM to shut down gracefully (the HTTP server
is drained and the WhatsApp socket is disconnected).

`GET /healthz` returns `200 ok` (no auth) so a supervisor can check liveness.

## HTTP contracts

### Inbound: WhatsApp → Erda (`POST $ERDA_INBOUND_URL`)

For every **accepted** message the bridge sends:

- Header `X-Bridge-Secret: <SHARED_SECRET>`
- Header `Content-Type: application/json`
- Body:

```json
{
  "from": "4915123456789@s.whatsapp.net",
  "chat": "4915123456789@s.whatsapp.net",
  "type": "text",
  "text": "hello",
  "mediaPath": "/tmp/erda-bridge/ab12cd34.ogg",
  "mimeType": "audio/ogg; codecs=opus",
  "messageId": "3EB0...",
  "timestamp": 1748600000
}
```

Field semantics:

| Field       | Notes                                                                                   |
| ----------- | --------------------------------------------------------------------------------------- |
| `from`      | Bare sender JID (the owner).                                                             |
| `chat`      | JID to reply to (the owner). Pass this as `to` on `/send`.                               |
| `type`      | One of `text` \| `audio` \| `image`.                                                    |
| `text`      | **text:** the message body. **image:** the caption (`""` if none). **audio:** `""`.     |
| `mediaPath` | Absolute path to the downloaded file. Present for `audio`/`image`; **omitted** for `text`. |
| `mimeType`  | Media mimetype. Present for `audio`/`image`; **omitted** for `text`.                     |
| `messageId` | WhatsApp message ID.                                                                     |
| `timestamp` | Unix seconds.                                                                            |

`mediaPath` / `mimeType` use `omitempty`, so they are absent from the JSON for
text messages.

**Filtering rules** (anything not matching is dropped):

1. Group / broadcast messages are ignored.
2. Only messages whose sender's **bare user part** equals `OWNER_JID`'s bare
   user are relayed (device/agent suffixes ignored).
3. Only `text`, `audio` (incl. PTT voice notes), and `image` are forwarded.
   Stickers, video, documents, etc. are logged and skipped.

Media (audio/image) is downloaded and written to `MEDIA_DIR` with a random
filename and an extension derived from the mimetype (e.g. `.ogg` for
`audio/ogg`, `.jpg` for `image/jpeg`).

Erda is expected to return **202** and process asynchronously; the reply comes
back later via `/send`. A non-2xx response is logged as a warning, not fatal.

### Outbound: Erda → WhatsApp (`POST /send` on `BRIDGE_LISTEN`)

- Header `X-Bridge-Secret: <SHARED_SECRET>` — required, else **401**.
- Body:

```json
{ "to": "4915123456789@s.whatsapp.net", "text": "your reply" }
```

- **200 `ok`** on success.
- **400** for invalid JSON, missing `to`/`text`, or an unparseable JID.
- **401** if the secret is missing/wrong.
- **500** with a short error string if the WhatsApp send fails.

Example:

```bash
curl -X POST http://127.0.0.1:8088/send \
  -H "X-Bridge-Secret: $SHARED_SECRET" \
  -H "Content-Type: application/json" \
  -d '{"to":"4915123456789@s.whatsapp.net","text":"hi from Erda"}'
```

### Health (`GET /healthz`)

No auth. Returns `200 ok`.

## Files

- `main.go` — config, lifecycle, WhatsApp client setup, QR/connect, graceful shutdown.
- `relay.go` — inbound handling: filtering, typing, media download, forward to Erda.
- `send.go` — outbound HTTP server: `/send` and `/healthz`.
- `.env.example` — all environment variables with placeholder values.

## Notes

- Built and tested against `whatsmeow` pinned in `go.mod`. The whatsmeow API
  changes over time; this targets the current `Download(ctx, DownloadableMessage)`
  and context-taking `sqlstore.New(ctx, ...)` / `GetFirstDevice(ctx)` signatures.
- `go vet ./...` is clean and the binary builds with `CGO_ENABLED=0`.
