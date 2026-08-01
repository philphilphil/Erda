# ErdaBridge (macOS)

A minimally-privileged macOS bridge that lets Erda (running in Docker on `leela`) create, list and
complete **Apple Reminders** in explicitly allowlisted lists — and nothing else.
Chain: `Erda → HTTP + bearer token → ErdaBridge → EventKit → Reminders`.

**Status: M0–M7 complete** — `BridgeCore`, `BridgeStore`, `BridgeHTTP`, `BridgeEventKit`, the setup
UI, and hardening (forbidden-API lint, binary-level symbol checks, an audit-redaction property
test). 358 tests / 44 suites pass, `make test` and `./scripts/bundle.sh` are clean. **The app has
never been run against real Reminders data or installed to `/Applications`** — that verification is
this document's main job: see [Needs a human at the screen](#-needs-a-human-at-the-screen) and the
[manual verification checklist](#manual-verification-checklist).

---

## Contents

- [Needs a human at the screen](#-needs-a-human-at-the-screen)
- [Build and run](#build-and-run)
- [First-run setup order](#first-run-setup-order)
- [Code signing](#code-signing)
- [The macOS Application Firewall](#the-macos-application-firewall)
- [Network: the DHCP reservation](#network-the-dhcp-reservation)
- [Token rotation](#token-rotation)
- [Login Items](#login-items)
- [Uninstall](#uninstall)
- [Threat model](#threat-model)
- [Manual verification checklist](#manual-verification-checklist)
- [Reference](#reference) — architecture, Setup UI internals, M0 findings, HTTP behaviour table

---

## ⚠ Needs a human at the screen

Nothing below can be done from an agent session — no `open`/launch, no granting or revoking
Reminders, no token rotation, nothing under `~/Library`. All of it needs the app running with real
permissions on Phil's Mac.

1. **Reachable from `leela`**, and confirm whether a **Local Network privacy alert** appears:
   ```bash
   curl -H 'Authorization: Bearer <token>' http://<bind-ip>:<port>/v1/status
   ```
   The design dossier predicted inbound accepts are *not* gated by Local Network privacy (that
   permission governs outbound LAN scans, e.g. Bonjour); M0 did not exercise this because the
   Application Firewall prompt intervened first. Still unconfirmed against a real client on
   another host. If an alert appears, that is a one-time approval to document here.
2. **Grant Reminders access** via the Setup window and confirm the TCC prompt names **ErdaBridge**
   and quotes `NSRemindersFullAccessUsageDescription` ("ErdaBridge reads and creates reminders in
   the lists you allow, …"). Confirm the status flips to `full access` with a non-zero list count.
3. **Allowlist a real list**, assign it an alias, and run the full create → list → complete cycle
   against Reminders.app — this bridge has only ever talked to `FakeReminders` in tests.
4. Work through the [manual verification checklist](#manual-verification-checklist) below.
5. **Install to `/Applications`** (`make install`), re-approve the Application Firewall at the new
   path, add to Login Items, and point `leela`'s `.env` at the bridge (`AppleBridge__*`).
6. **End-to-end**: `dotnet test Erda.Tests/Erda.Tests.csproj`, then `make dev` on `leela` with
   `AppleBridge__*` set, and ask Erda over WhatsApp to add, list and complete a reminder — confirm
   each in Reminders.app. Then close the MacBook lid and repeat: the tools must return a readable
   "bridge unreachable" message, never an exception.

---

## Build and run

```bash
make bundle    # swift build -c release + assemble + codesign + verify + print the DR
make test      # scripts/lint-forbidden.sh, then swift test (358 tests / 44 suites)
make run       # bundle, then `open` the signed .app
make install   # copy to /Applications/ErdaBridge.app
make clean
```

> ## ⚠ Never `swift run`
> A bare SwiftPM binary has no bundle identifier, so TCC attributes the Reminders request to the
> **terminal emulator**, not ErdaBridge. You would grant Terminal access to Reminders and learn
> nothing about this app. `make run` always bundles + `open`s the signed `.app` — use it, always.
>
> Never ad-hoc sign (`--sign -`) either: TCC then falls back to the cdhash, and M0 measured that
> the cdhash changes on every relink, so every rebuild would re-prompt and orphan the previous
> grant.

---

## First-run setup order

Do these in order — later steps depend on earlier ones:

1. **`make run`** — menu bar item appears, no Dock icon (`LSUIElement`).
2. **Grant Reminders access** from the Setup window (menu bar icon → **Setup…**). Confirms as
   `full access` with a non-zero list count.
3. **Choose lists and assign aliases** in the list picker (title, source, writability shown for
   each). An alias is `^[a-z0-9][a-z0-9_-]{0,31}$`, e.g. `inbox`, `groceries`. No alias ever falls
   back to a default list — an unrecognized, removed or broken alias fails closed.
4. **Choose the bind address.** The picker offers the addresses currently configured on this Mac;
   nothing is auto-selected — see [Network: the DHCP reservation](#network-the-dhcp-reservation)
   for why the choice must be a LAN address with a DHCP reservation, not "whatever is available."
5. **Generate the token.** Shown exactly once with a Copy button; only a salted digest is
   persisted. Save it somewhere durable now — there is no way to retrieve it again, only to
   rotate it (see [Token rotation](#token-rotation)).
6. **Start the listener.** The Setup window's readiness line goes green only when Reminders access
   is `full access`, at least one alias is usable, a token exists, and the listener is bound to a
   non-loopback address.
7. **Configure Erda's `.env`** on `leela` (`.env.example` documents these):
   ```
   AppleBridge__Enabled=true
   AppleBridge__BaseUrl=http://<bind-ip>:<port>
   AppleBridge__ApiKey=<the token from step 5>
   AppleBridge__TimeoutSeconds=5
   ```
   Then restart Erda (`make deploy` in production, or restart `make dev`).

The first time through, also expect the [Application Firewall prompt](#the-macos-application-firewall)
the moment the listener binds — a second, independent one-time approval from step 6.

---

## Code signing

**The team-ID-pinned designated requirement is the default** (`scripts/bundle.sh`).
`ERDA_BRIDGE_DR=default make bundle` falls back to codesign's own DR. **Use the default** — it is
what M0's decisive rebuild test validated, and it is the one stable across the 2027-03-24
certificate renewal (below).

### Default (what we ship) — team-ID pinned

```
designated => identifier "de.philippbaum.erdabridge" and anchor apple generic and certificate leaf[subject.OU] = "6CR38F5CRX" and certificate 1[field.1.2.840.113635.100.6.2.1] /* exists */
```

`codesign --verify --strict --deep` passes: `valid on disk` / `satisfies its Designated Requirement`.
`subject.OU` is the team ID, which is **stable across certificate renewals**.
**The TCC grant is recorded under this DR.**

### The escape hatch — codesign's default, which pins the leaf CN

```
designated => identifier "de.philippbaum.erdabridge" and anchor apple generic and certificate leaf[subject.CN] = "Apple Development: philipp.baum@me.com (397C268TA6)" and certificate 1[field.1.2.840.113635.100.6.2.1] /* exists */
```

M0 measured this and it confirms the design dossier's *inferred* claim: codesign's default DR for
an Apple Development identity pins the leaf certificate's **common name including the
`(397C268TA6)` suffix**. That suffix is per-certificate and **the cert expires 2027-03-24**; a
renewal issuing a different suffix would change the app's code identity under this DR, silently
orphaning the Reminders grant and potentially hanging a login-item start on a Keychain ACL prompt.
That measurement is why the team-ID DR became the default — it does not embed the suffix, so a
renewal is a non-event for it.

Keep both texts above for comparison after any future re-sign or cert renewal: run
`codesign -d -r- ErdaBridge.app` and diff against whichever of the two this build should match.

### Syntax correction to the design dossier

`--requirements` takes the requirement *source* prefixed with `=`, e.g.
`--requirements '=designated => …'`. The `@file` form given in the dossier does **not** work — it
fails with `invalid requirement specification`.

### Certificate renewal — February/March 2027 reminder

The signing cert (`Apple Development: philipp.baum@me.com (397C268TA6)`, team `6CR38F5CRX`) expires
**2027-03-24**. `--timestamp` keeps existing signatures valid past expiry, but a renewal changes the
leaf CN. With the team-ID DR that should be a non-event; verify anyway when it happens
(`make bundle` then `codesign -d -r-` and diff against the text above), and confirm the Reminders
grant survives without a re-prompt.

---

## The macOS Application Firewall

Separate from TCC and separate from codesign. If macOS's Application Firewall (System
Settings → Network → Firewall) is on, it holds every inbound flow to a newly-seen app pending a
one-time human decision — *"Do you want the application ErdaBridge to accept incoming network
connections?"* — even for LAN traffic that loops back through `lo0` to reach a non-loopback bind
address on the same Mac. Until approved, the kernel completes the TCP handshake but the process
never receives the connection: it looks exactly like a silent hang, not a refused connection.

**Approval is keyed on the app's path**, not on its designated requirement or cdhash — re-signing
the bundle in place (even switching DRs) keeps the approval, but a new path needs a fresh one. That
means:

- The dev build at `macos-bridge/.build/ErdaBridge.app` needs approving once.
- Installing to `/Applications/ErdaBridge.app` (`make install`) is a **new path** and needs
  approving again:
  ```bash
  sudo /usr/libexec/ApplicationFirewall/socketfilterfw --add /Applications/ErdaBridge.app
  sudo /usr/libexec/ApplicationFirewall/socketfilterfw --unblockapp /Applications/ErdaBridge.app
  ```
- Check current state any time with:
  ```bash
  /usr/libexec/ApplicationFirewall/socketfilterfw --listapps
  ```
- *"Automatically allow downloaded signed software"* does **not** cover an Apple-Development-signed
  app (no Developer ID, not notarized), so the one-time prompt is unavoidable at each new path.

If `curl` from `leela` hangs with zero bytes and `lsof -nP -iTCP:<port> -sTCP:LISTEN` shows the
process bound but `netstat` shows the connection `ESTABLISHED` with nothing accepted, this is
almost certainly why — check `log show` for `com.apple.ALF.ApplicationFirewall` first.

---

## Network: the DHCP reservation

**The bridge's bind address must have a DHCP reservation on the FRITZ!Box, or the listener stops
binding the moment the lease changes.** This already happened once during development: the address
recorded during M0 was `192.168.178.106` on interface `en10`; by M5 the same Mac had
`192.168.178.103` on `en0` — a different address *and* a different interface, from the same router
handing out a new lease after a Wi-Fi reconnect. `ServerSupervisor` backs off and retries an address
that has moved (`EADDRNOTAVAIL`), so the app doesn't crash, but the bridge is unreachable the whole
time and nothing pages anyone — the menu bar icon just turns red.

The address is stored in the bridge's own settings, chosen once in the Setup window (see
[First-run setup order](#first-run-setup-order)), not compiled in — but a stored *address* still has
to remain valid on the interface. Set a static DHCP reservation for this Mac's MAC address in the
FRITZ!Box's network settings so the leased address stops moving, then pick that address in Setup and
put the same value in `leela`'s `AppleBridge__BaseUrl`.

If the address ever does drift despite the reservation (new router, Wi-Fi vs. Ethernet switch,
etc.), the fix is: open the Setup window, re-pick the new address, confirm `lsof -nP -iTCP:<port>
-sTCP:LISTEN` shows the new bind, then update `AppleBridge__BaseUrl` in `leela`'s `.env` and
restart Erda.

---

## Token rotation

**Generate**/**Rotate** in the Setup window shows the token exactly once with a Copy button. Only a
salted digest is stored (Keychain, service `de.philippbaum.erdabridge`, account `api-token`).
**Rotating invalidates the old token immediately** — every in-flight and future request bearing it
gets `401`. There is no grace window, so rotate and update `leela` in the same breath:

1. Setup window → **Rotate token** → copy the new value.
2. Immediately update `AppleBridge__ApiKey` in `leela`'s `.env`.
3. Restart Erda.

`ErdaBridge --rotate-token` performs the same rotation headlessly (prints the token id and the
token once to stdout, stores nothing but the digest) — useful for a scripted rotation, but the
"update `.env` in the same breath" rule still applies.

---

## Login Items

This build **deliberately does not use `SMAppService`** (that requires an App Sandbox entitlement
path this project isn't taking — see [Threat model](#threat-model)), so login-launch is set up by
hand, once, after installing:

1. Install first: `make install` (copies to `/Applications/ErdaBridge.app`), then approve the
   [Application Firewall](#the-macos-application-firewall) at that path.
2. **System Settings → General → Login Items & Extensions** → under "Open at Login", click **+**
   and add `/Applications/ErdaBridge.app`.
3. Log out and back in (or just relaunch) to confirm it starts automatically, menu bar item and
   all, with no Dock icon.

There is no in-app toggle for this and no code that touches Login Items — it is pure System
Settings state, which is also why it survives an app rebuild untouched as long as the path doesn't
change.

---

## Uninstall

In order — later steps assume earlier ones are done:

1. **System Settings → General → Login Items & Extensions** → remove ErdaBridge from "Open at
   Login" (skip if you never added it).
2. **Quit the app** from the menu bar (or `killall ErdaBridge` if the menu is unresponsive).
3. **Revoke Reminders access**: System Settings → Privacy & Security → Reminders → turn off
   ErdaBridge (or remove it from the list entirely).
4. **Delete the Keychain item**: Keychain Access.app → login keychain → search
   `de.philippbaum.erdabridge` → delete the `api-token` password item. Or from Terminal:
   ```bash
   security delete-generic-password -s de.philippbaum.erdabridge -a api-token
   ```
5. **Delete the app**: `rm -rf /Applications/ErdaBridge.app` (and/or the `.build/` copy under this
   repo, which `make clean` also removes).
6. **Delete application state**:
   ```bash
   rm -rf ~/Library/Application\ Support/de.philippbaum.erdabridge/
   rm -rf ~/Library/Logs/ErdaBridge/
   ```
   This drops the SQLite allowlist/id-map/idempotency store and the JSONL audit log. There is
   nothing else on disk — no App Sandbox container, no cache directory, no defaults domain beyond
   what AppKit itself may have written to `~/Library/Preferences` for window state (harmless to
   leave, safe to also delete if you want a completely clean slate:
   `rm -f ~/Library/Preferences/de.philippbaum.erdabridge.plist`).
7. On `leela`, set `AppleBridge__Enabled=false` in `.env` and restart Erda so the tools stop being
   registered (they already fail soft if left enabled and unreachable, but there is no reason to
   leave them on).

The Application Firewall's per-path allow entry for `/Applications/ErdaBridge.app` is left behind
by `socketfilterfw`; it is inert once the app is gone and does not need separate cleanup.

---

## Threat model

**Deliberately unsupported, by design, not by oversight:**

- No delete or edit of reminders — only `create`, `list`, `complete`. Complete-of-completed is an
  idempotent no-op; there is no "uncomplete", no field edit, no move-between-lists.
- No calendar access at all — Reminders only.
- No shell, no AppleScript, no Shortcuts, no scripting-bridge path of any kind. Enforced twice:
  the module boundaries (only `BridgeEventKit` links `EventKit`; nothing links `ScriptingBridge` or
  `WebKit`) are compiler-enforced, and `scripts/lint-forbidden.sh` (wired into `make test`) greps
  `Sources/` for `Process`, `NSTask`, `NSAppleScript`, `OSAScript`, `SBApplication`, `NSWorkspace`,
  `WKWebView`, `posix_spawn`, `system(`, `popen`, and `import ScriptingBridge|WebKit|Intents|AppIntents`.
  `scripts/bundle.sh` additionally scans the **linked binary's** undefined symbols for the same
  APIs, so a transitive dependency pulling one in would also be caught.
- No remote route that can change the allowlist, the token, permissions, or the bind-address/config
  — those are exclusively local Setup-window actions (or `--rotate-token` at the terminal on this
  Mac). The four HTTP routes (`GET /v1/status`, `POST /v1/reminders`, `GET /v1/reminders`,
  `POST /v1/reminders/{id}/complete`) touch reminders in already-allowlisted lists and nothing
  else — there is no route shaped like "add a list" or "issue a token."
- Missing functionality is never "solved" by adding a macOS permission or a shell fallback — if a
  capability needs one of the above, the answer is "not in scope," not "add it quietly."

**Two deliberate relaxations of the original spec** (both recorded as accepted-cost decisions, not
gaps that slipped through):

1. **Plain HTTP on the LAN, bearer token, instead of loopback-only + Tailscale Serve.** Both
   machines are on the same home Wi-Fi, and setting up Tailscale for this round was out of scope.
   **Accepted cost: the bearer token crosses the home Wi-Fi in cleartext** on every request.
   Anyone with access to that Wi-Fi (or anything upstream of it, e.g. a compromised router) can
   read the token off the wire and would then have the same reminders-create/list/complete access
   Erda has, for the allowlisted lists, until the token is rotated. Mitigations in place: the token
   is only ever useful against this bridge's narrow API (no lateral capability), rotation is cheap
   (see [Token rotation](#token-rotation)), and the allowlist itself bounds the blast radius to
   lists a human explicitly opted in.
2. **No App Sandbox, no `SMAppService`.** The app runs with the ambient privileges of Phil's user
   account rather than inside a sandbox container, and login-launch is a manual
   [Login Items](#login-items) entry rather than a programmatic `SMAppService` registration. This
   is what makes the legacy (non-data-protection) Keychain usable without a provisioning profile —
   see the comment in `Sources/BridgeStore/KeychainTokenStore.swift` — and keeps the whole project
   buildable as plain SwiftPM with no Xcode project, no entitlements beyond hardened-runtime
   defaults, and no Apple Developer Program enrollment. The cost is that the app is not sandboxed:
   nothing in this codebase currently uses that headroom (no `Process`, no file access outside its
   own `~/Library` directories, no scripting bridges — see above), but the OS-level backstop a
   sandbox would provide is absent.

---

## Manual verification checklist

Everything here needs the app running with real Reminders data and a real network — none of it can
be done from an agent session. Work through it once before relying on the bridge, and again after
any change to `BridgeEventKit`, the router, or the store.

**Permissions and revocation**

- [ ] Grant Reminders access → Setup window shows `full access`, non-zero list count.
- [ ] Revoke Reminders access in System Settings while the bridge is running → next request returns
      `503 reminders_unavailable`, no crash, no partial write.
- [ ] Re-grant → bridge recovers without a restart (confirm via `eventStoreChangedNotification`
      handling, not just a relaunch).

**Allowlist enforcement**

- [ ] Allow one list, assign an alias. `POST /v1/reminders` with that alias creates the item and it
      appears in Reminders.app.
- [ ] `GET /v1/reminders` returns items from allowed lists only — a non-allowlisted list's items are
      invisible, not filtered client-side.
- [ ] Move a reminder that was created via the bridge into a **non-allowlisted** list, then try to
      complete it by id → `404`, not a silent success.
- [ ] Complete an already-completed reminder → `200`, idempotent no-op, not an error.
- [ ] Delete the allowlisted list itself in Reminders.app, then make a request against its alias →
      alias shows `broken` in the Setup window with title/source candidates offered, **nothing
      re-bound automatically**, and the API returns `409` rather than silently reusing another list.

**Idempotency**

- [ ] Two concurrent `POST /v1/reminders` with the same `Idempotency-Key` and the same body →
      exactly one reminder created; the second response carries `Idempotency-Replayed: true`.
- [ ] Same key, different body → `409 idempotency_key_reuse`.
- [ ] Same key while the first request is still in flight → `409 request_in_progress`.

**Token rotation**

- [ ] Rotate the token in the Setup window → the **old** token gets `401` on the very next request,
      no grace period.
- [ ] The new token works immediately without restarting the app.

**Network**

- [ ] `lsof -nP -iTCP:<port> -sTCP:LISTEN` shows a bind to the configured LAN address **only** — not
      `0.0.0.0`, not `127.0.0.1` (unless loopback was deliberately chosen for local-only testing).
- [ ] The port is unreachable from a host other than the one it's supposed to be reachable from
      (sanity-check the allowlist is a bind choice, not a firewall rule this app doesn't have).
- [ ] Turn Wi-Fi off with the listener bound → status goes red within ~30s and starts retrying;
      turn it back on → rebinds without a relaunch.
- [ ] From `leela`: full status → create → list → complete round trip over the real LAN.

**Firewall and signing**

- [ ] A fresh path (e.g. after `make install`) re-prompts the Application Firewall exactly once;
      approving it with the commands in [that section](#the-macos-application-firewall) fixes it
      without a rebuild.
- [ ] `codesign -d -r- ErdaBridge.app` matches the [team-ID DR text](#code-signing) recorded above.

---

## Reference

Architecture, the Setup UI's internal behaviour, and the M0 risk-spike findings — read these when
you need the *why*, not just the *how*.

### Architecture

Swift Package (`Package.swift`, all text, no `.xcodeproj`) plus `scripts/bundle.sh` that assembles
and codesigns `ErdaBridge.app`. Module boundaries *are* the security architecture and are enforced
by the compiler, not by discipline:

```
BridgeCore      pure logic, Sendable DTOs, no frameworks    ← the RemindersService seam
BridgeStore     raw SQLite3 + Keychain + JSONL audit sink
BridgeHTTP      SwiftNIO/NIOHTTP1 — does NOT link EventKit
BridgeEventKit  the ONLY target that imports EventKit
ErdaBridgeApp   @main SwiftUI MenuBarExtra + setup window
```

- **SwiftNIO + NIOHTTP1 directly**, not Hummingbird: Hummingbird pulls 16 packages including
  `async-http-client` (outbound HTTP — the exact primitive this app must not have), `swift-nio-ssl`
  and `swift-nio-http2`. NIOHTTP1 already provides the two caps needed as first-class API
  (`NIOHTTPDecoderLimitConfiguration` for headers, `NIOHTTPServerRequestAggregator(maxContentLength:)`
  which emits 413 itself). `NIOAsyncChannel` makes each connection a plain `async` Task, so handlers
  can `await` the EventKit actor without blocking an event loop.
- **Raw `import SQLite3`** (in the SDK, zero deps) for allowlist / id-map / idempotency; a rotating
  **JSONL file** for the audit log so it stays `tail -f`-able and can't be locked out by a
  transaction.
- **EventKit behind an actor with a custom `DispatchSerialQueue` executor** — serialises mutations
  *and* keeps blocking `saveReminder:` calls off the cooperative pool. EventKit types are
  non-`Sendable` and must never cross an isolation boundary; `[EKReminder]` is mapped to DTOs inside
  the fetch completion closure.
- **Identifier drift is the main domain risk.** `EKCalendar.calendarIdentifier` and
  `EKCalendarItem.calendarItemIdentifier` are explicitly *not* sync-proof. An alias whose calendar
  no longer resolves goes to state `broken` and fails closed; re-binding by title is **never**
  automatic (that is how you'd write into a stranger's shared list after a resync) — the local UI
  proposes candidates and a human confirms. A dangling reminder id is always a `404`.

### API

`GET /v1/status`, `POST /v1/reminders`, `GET /v1/reminders`, `POST /v1/reminders/{id}/complete`.
Bearer token on all four including status. Errors are `{"error":"<snake_code>","requestId":"…"}`
with **no message field** — structurally impossible to leak a path or `NSError` description.
Request order per connection: admission → protocol gate → route table → auth → rate limit →
content negotiation → strict decode → idempotency → domain → audit (audit always runs, including
on rejection).

### Hardening (M7)

- `scripts/lint-forbidden.sh` — greps `Sources/` for the forbidden-API list in the
  [threat model](#threat-model) above; run standalone or via `make test` / `make lint`.
- `scripts/bundle.sh` prints `otool -L` and scans `nm -u` output for the same symbol family after
  every signed build, so a new dependency shows up in build output without a separate step.
- `Tests/BridgeHTTPTests/AuditTests.swift`'s `linesCarryNoUserContent` test drives 60+ random
  Unicode titles/notes (plus fixed adversarial strings: a vault path, the bearer token, a token-
  shaped string, an RTL override, a SQL-injection attempt) through the **full handler path** against
  `FakeReminders`, and asserts the emitted JSONL audit line contains none of them and no `/Users/`
  substring. `AuditEvent` has no free-form `String` field, so this guards a structural guarantee
  rather than hunting for a leak, which is why it's cheap to run over adversarial input.

### Setup UI (M5)

Everything the bridge needs is configured from the menu bar item → **Setup…**. Nothing here is
reachable over HTTP — see [Threat model](#threat-model).

**The bind address is stored, not compiled in.** Earlier builds hardcoded `192.168.178.106`; that
address moved with the DHCP lease and the app could no longer bind at all (see
[Network: the DHCP reservation](#network-the-dhcp-reservation)). The address and port now live in
the `meta` table (`bind_ip`, `port`) and are chosen in the Setup window.

- **Nothing is auto-selected.** The picker *offers* the addresses currently configured on this Mac
  — that is discovery — but the choice is always a human's. Binding "whatever is available" could
  publish the bridge on a café network or a phone hotspot.
- `0.0.0.0`, `::`, hostnames, and any address not on a local interface right now are refused, both
  when saving and at every start attempt.
- Loopback **is** selectable, for testing on this Mac only. The status line then says so and the
  readiness light goes amber: Erda cannot reach a loopback bind.
- Link-local IPv6 (`fe80::/10`) is not offered — it can only be bound with a zone index, which the
  validator refuses.

**What the supervisor does when the address goes away** — `ServerSupervisor` re-reads the stored
choice and re-validates it on **every** attempt:

| Situation | Behaviour |
|---|---|
| Nothing configured | No bind. Red state, no retry — there is nothing to retry. |
| Address not on any interface (DHCP moved, Wi-Fi off) | No bind. Red state, retried 2s → 4s → … → 60s, because the address usually comes back. |
| Port in use / `bind(2)` failed | Same backoff. |
| Wildcard, hostname, port < 1024 | Red state, **no** retry: it will never become valid on its own. |
| Bound address disappears while running | The listener is torn down and the retry loop re-entered. It is never reported as healthy when it is unreachable. |

The menu bar icon changes shape with the state (`checklist` / triangle / octagon), so a bridge that
cannot serve a request looks different without opening the window.

**Lists, aliases and broken bindings.** The list picker shows every reminder list with its title,
source and whether it is writable. An alias is `^[a-z0-9][a-z0-9_-]{0,31}$`; an alias already in use
must be unbound before it can be re-pointed. When a binding's `calendarIdentifier` no longer
resolves — which an iCloud full sync can cause — the alias goes to **broken** and fails closed; the
window offers *candidate* lists ordered by title/source similarity, and a confirmation dialog names
the list before anything is written. Re-binding by title never happens automatically.

**Readiness is a conjunction.** The window says **Ready** only when Reminders access is
`full access`, at least one alias is usable, a token exists, and the listener is bound to a
non-loopback address. Anything less is amber or red with the specific reason.

### M0 findings

Recorded 2026-07-31 on macOS 26.5.2, Xcode 26.6, Swift 6.3.3, Apple Silicon.

**Project form: SwiftPM + `scripts/bundle.sh` works.** `swift build -c release --arch arm64` + a
hand-assembled `.app` + `codesign` produces a valid, hardened-runtime, Apple-Development-signed
bundle. No `.xcodeproj` and no `xcodegen` needed — the decisive rebuild test (edit a string,
`make bundle`, relaunch, confirm no second TCC prompt) passed.

Confirmed constraints from the design dossier:

- The `@main` file must **not** be named `main.swift` (it is `ErdaBridgeApp.swift`).
- `NSPrincipalClass = NSApplication` + `CFBundlePackageType = APPL` are required in `Info.plist`.
- No SwiftPM `resources:` — resources go into `Contents/Resources/` by the bundle script.

**Rebuild determinism.** The DR text is byte-identical across rebuilds; the cdhash is not:

| Build | Source | cdhash |
|---|---|---|
| 1 | baseline | `0225a7d418884f5242aac830f04c0310dcec0738` |
| 2 | one string changed | `b747583177eaf18b55a4c93e08db48a831ad2547` |
| 3 | string reverted (relinked) | `144928c53ef842fbbc63f21ee869da95a6709b89` |

Build 3 has source identical to build 1 but a different cdhash — the link step is not reproducible
(new `LC_UUID` per link). This is exactly why ad-hoc signing would re-prompt on every rebuild, and
why the certificate-chain-based DR is the thing that has to stay stable.

**The Application Firewall gates the LAN listener** — see the
[dedicated section](#the-macos-application-firewall) above; this is where it was first discovered,
via `lsof` showing the process bound but no accepted socket, and the system log naming
`com.apple.ALF.ApplicationFirewall`.

**HTTP layer behaviour** (verified over a temporary `127.0.0.1` bind, to separate "firewall" from
"our bug"; the bind IP was restored afterward):

| Request | Response |
|---|---|
| `GET /v1/status` + valid bearer | `200` `{"ok":true}` |
| `GET /v1/status`, no `Authorization` | `401` `{"error":"unauthorized"}` |
| `GET /v1/status`, wrong token | `401` `{"error":"unauthorized"}` |
| `GET /nope` | `404` `{"error":"not_found"}` |
| `POST /v1/status` | `405` + `Allow` (M0 measured `404`; M3 added the proper `405`) |
| `--http1.0` | `505` `{"error":"unsupported_http_version"}` |
| 17 KiB body | `413` — emitted by `NIOHTTPServerRequestAggregator`, not by our code |
| 17 KiB header | `400` — from `HTTPServerProtocolErrorHandler` via the 16 KiB decoder limits |

So the dossier's documented caps work as claimed: `NIOHTTPDecoderLimitConfiguration` for headers and
`NIOHTTPServerRequestAggregator(maxContentLength:)` for bodies both enforce themselves.

**`otool -L`** at M0 (before `BridgeStore`/`BridgeEventKit` added `libsqlite3`/`EventKit`/
`CryptoKit`/`Security`, all visible in the current `make bundle` output):

```
/usr/lib/libSystem.B.dylib
/System/Library/Frameworks/AppKit.framework/Versions/C/AppKit
/System/Library/Frameworks/EventKit.framework/Versions/A/EventKit
/System/Library/Frameworks/Foundation.framework/Versions/C/Foundation
/System/Library/Frameworks/SwiftUI.framework/Versions/A/SwiftUI
/usr/lib/libc++.1.dylib
/usr/lib/libobjc.A.dylib
/usr/lib/swift/libswiftCore.dylib
... (weak libswift* overlay dependencies from SwiftUI/AppKit, not from our code)
```

Nothing surprising then or now. SwiftNIO is statically linked (no dylib entries). No WebKit, no
ScriptingBridge. `nm -u` showed no `NSTask` / `OSAScript` / `WKWebView` / AppleScript symbols at M0;
`scripts/bundle.sh` now checks this on every build (see [Hardening](#hardening-m7)).

**Miscellaneous:**

- The hardened runtime already refuses Apple Events, which is exactly the intended posture:
  `tccd: Prompting policy for hardened runtime; service: kTCCServiceAppleEvents requires entitlement
  com.apple.security.automation.apple-events but it is missing`. We ship no entitlements file, so
  this cannot change without a deliberate edit.
- `spctl -a -vv` fails ("not notarized"). Expected and irrelevant: a locally built app never gets a
  `com.apple.quarantine` xattr, so Gatekeeper is not consulted on launch.
- Swift 6 language mode compiles clean with zero warnings. Two things needed care: capture a `let`
  copy of `NIOHTTPDecoderLimitConfiguration` (not a `var`) in the `@Sendable` child-channel
  initializer, and capture `context.channel` (which is `_NIOPreconcurrencySendable`) rather than the
  non-`Sendable` `ChannelHandlerContext` in a write completion.
