# ErdaBridge (macOS)

A narrow macOS bridge that lets Erda (running in Docker on `leela`) create, list and complete
**Apple Reminders**, and create and read **Apple Calendar** events — and nothing else. Lists and
calendars are addressed by their real name, exactly as they read in Reminders.app and Calendar.app.
Chain: `Erda → HTTP + bearer token → ErdaBridge → EventKit → Reminders / Calendar`.

**It can read and write every reminder list on this Mac, and it can read every event in every
calendar.** Both are deliberate decisions, not oversights — see the [threat model](#threat-model).

**Status: M0–M7 complete, plus calendar support** — `BridgeCore`, `BridgeStore`, `BridgeHTTP`,
`BridgeEventKit`, the setup UI, and hardening (forbidden-API lint, binary-level symbol checks, an
audit-redaction property test). 471 tests / 50 suites pass, `make test` and `./scripts/bundle.sh`
are clean. **The app has never been run against real Reminders or Calendar data, or installed to
`/Applications`** — that verification is this document's main job: see
[Needs a human at the screen](#-needs-a-human-at-the-screen) and the
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
Reminders or Calendar access, no token rotation, nothing under `~/Library`. All of it needs the app
running with real permissions on Phil's Mac.

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
   your lists, …"). Confirm the status flips to `full access` with a non-zero list count.
3. **Grant Calendar access** — a **separate button and a separate prompt**, because macOS records
   the two grants independently. Confirm it quotes `NSCalendarsFullAccessUsageDescription`
   ("ErdaBridge reads your calendar events and creates new ones, …") and that the prompt is the
   *full access* one, not write-only. Confirm the status flips to `full access` with a non-zero
   calendar count. Note that granting this lets the bridge read every event on this Mac — see the
   [threat model](#threat-model) before clicking it.
4. **Run the full reminder create → list → complete cycle** against a real list in Reminders.app,
   and **create → list an event** in a real calendar — this bridge has only ever talked to
   `FakeReminders`/`FakeCalendar` in tests. Remember that **an event cannot be deleted through the
   bridge**: whatever you create you remove by hand in Calendar.app.
5. Work through the [manual verification checklist](#manual-verification-checklist) below.
6. **Install to `/Applications`** (`make install`), re-approve the Application Firewall at the new
   path, add to Login Items, and point `leela`'s `.env` at the bridge (`AppleBridge__*`).
7. **End-to-end**: `dotnet test Erda.Tests/Erda.Tests.csproj`, then `make dev` on `leela` with
   `AppleBridge__*` set, and ask Erda over WhatsApp to add, list and complete a reminder, and to
   put an appointment in a calendar and read back what's coming up — confirm each in Reminders.app
   and Calendar.app. Then close the MacBook lid and repeat: the tools must return a readable
   "bridge unreachable" message, never an exception.

---

## Build and run

```bash
make bundle    # swift build -c release + assemble + codesign + verify + print the DR
make test      # scripts/lint-forbidden.sh, then swift test (471 tests / 50 suites)
make run       # bundle, then `open` the signed .app
make install   # copy to /Applications/ErdaBridge.app
make clean
```

> ## ⚠ Never `swift run`
> A bare SwiftPM binary has no bundle identifier, so TCC attributes the Reminders and Calendar
> requests to the **terminal emulator**, not ErdaBridge. You would grant Terminal access to your
> reminders and calendars and learn nothing about this app. `make run` always bundles + `open`s the
> signed `.app` — use it, always.
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
3. **Grant Calendar access** — a second button, a second prompt, a second TCC record. Denying it
   leaves the reminder half working exactly as before; the calendar routes then answer
   `503 calendar_unavailable` and the readiness light goes amber rather than green. Read the
   [threat model](#threat-model) first: this grant is full read access to every event on this Mac.
4. **Note the list and calendar names.** There is nothing to configure here — every reminder list
   and every calendar is reachable — but the Setup window shows the exact titles, and the title is
   how Erda addresses one. It also shows which calendars are writable: a subscribed or holiday
   calendar can be read but not written.
5. **Choose the bind address.** The picker offers the addresses currently configured on this Mac;
   nothing is auto-selected — see [Network: the DHCP reservation](#network-the-dhcp-reservation)
   for why the choice must be a LAN address with a DHCP reservation, not "whatever is available."
6. **Generate the token.** Shown exactly once with a Copy button; only a salted digest is
   persisted. Save it somewhere durable now — there is no way to retrieve it again, only to
   rotate it (see [Token rotation](#token-rotation)).
7. **Start the listener.** The Setup window's readiness line goes green only when Reminders access
   is `full access`, Calendar access is `full access`, a token exists, and the listener is bound to
   a non-loopback address.
8. **Configure Erda's `.env`** on `leela` (`.env.example` documents these) — one switch covers both
   capabilities, since they are the same app, the same token and the same address:
   ```
   AppleBridge__Enabled=true
   AppleBridge__BaseUrl=http://<bind-ip>:<port>
   AppleBridge__ApiKey=<the token from step 5>
   AppleBridge__TimeoutSeconds=5
   ```
   Then restart Erda (`make deploy` in production, or restart `make dev`).

The first time through, also expect the [Application Firewall prompt](#the-macos-application-firewall)
the moment the listener binds — a further, independent one-time approval from step 7.

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
   ErdaBridge (or remove it from the list entirely). Then do the same under **Calendars** — a
   separate row, and one that is easy to leave behind.
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
   This drops the SQLite id-map/idempotency store and the JSONL audit log. There is
   nothing else on disk — no App Sandbox container, no cache directory, no defaults domain beyond
   what AppKit itself may have written to `~/Library/Preferences` for window state (harmless to
   leave, safe to also delete if you want a completely clean slate:
   `rm -f ~/Library/Preferences/de.philippbaum.erdabridge.plist`).
7. On `leela`, set `AppleBridge__Enabled=false` in `.env` and restart Erda so the tools stop being
   registered (they already fail soft if left enabled and unreachable, but there is no reason to
   leave them on). This covers both the reminder and the calendar tools — one switch.

Events the bridge created stay in Calendar.app: it has no delete route, so nothing here removes
them. Search for them by title if you want them gone.

The Application Firewall's per-path allow entry for `/Applications/ErdaBridge.app` is left behind
by `socketfilterfw`; it is inert once the app is gone and does not need separate cleanup.

---

## Threat model

**Deliberately unsupported, by design, not by oversight:**

- No delete or edit of reminders — only `create`, `list`, `complete`. Complete-of-completed is an
  idempotent no-op; there is no "uncomplete", no field edit, no move-between-lists.
- No delete or edit of calendar events — only `create` and `list upcoming`. **Nothing the bridge
  writes to a calendar can be removed through the bridge**, by design: an API that could delete
  events is an API that can quietly empty a calendar, and the recovery from a bad create (delete it
  by hand in Calendar.app) is far cheaper than the recovery from a bad delete. There is also no
  recurrence, no attendees, no invitations, no alarms and no attachments — a create carries a
  calendar, a title, optional notes, a start, an end and a time zone, and the request DTO rejects
  any other key outright, so an unsupported feature is a loud 400 rather than a silently dropped
  field.
- No calendar *management*: no route creates, renames or deletes a calendar, and there is no
  `/v1/calendars` at all. `GET /v1/status` reports calendar names because a name is how a caller
  addresses one; that is a readout, not a handle.
- No shell, no AppleScript, no Shortcuts, no scripting-bridge path of any kind. Enforced twice:
  the module boundaries (only `BridgeEventKit` links `EventKit`; nothing links `ScriptingBridge` or
  `WebKit`) are compiler-enforced, and `scripts/lint-forbidden.sh` (wired into `make test`) greps
  `Sources/` for `Process`, `NSTask`, `NSAppleScript`, `OSAScript`, `SBApplication`, `NSWorkspace`,
  `WKWebView`, `posix_spawn`, `system(`, `popen`, and `import ScriptingBridge|WebKit|Intents|AppIntents`.
  `scripts/bundle.sh` additionally scans the **linked binary's** undefined symbols for the same
  APIs, so a transitive dependency pulling one in would also be caught.
- No remote route that can change the token, permissions, or the bind-address/config — those are
  exclusively local Setup-window actions (or `--rotate-token` at the terminal on this Mac). The six
  HTTP routes (`GET /v1/status`, `POST /v1/reminders`, `GET /v1/reminders`,
  `POST /v1/reminders/{id}/complete`, `POST /v1/calendar-events`, `GET /v1/calendar-events`) touch
  reminders and calendar events and nothing else — there is no route shaped like "create a list",
  "delete a calendar" or "issue a token."
- Missing functionality is never "solved" by adding a macOS permission or a shell fallback — if a
  capability needs one of the above, the answer is "not in scope," not "add it quietly." (Calendar
  access is the one permission that *was* added, and it was added with the cost written down below
  rather than quietly.)

**What it can reach: every reminder list on this Mac.**

The original design carried a per-list allowlist — a table of local aliases, each bound to one
`calendarIdentifier` a human had picked in the Setup window — because macOS grants EventKit reminder
access all-or-nothing, and the allowlist was the only place a narrower boundary could be drawn.

**That allowlist was removed on purpose.** Phil decided that a bridge with access to all of his own
reminder lists is the behaviour he wants on his own Mac, so the alias indirection was buying
complexity rather than containment. It has not been replaced with a weaker gate: there is no
implicit default list, no "primary" list, and no compensating filter. What it can reach is what the
heading says.

Concretely, anyone holding the bearer token can, on any list on this Mac:

- read every incomplete reminder — title, notes, due date, priority — via `GET /v1/reminders` with
  no filter;
- create a reminder in any list they can name;
- complete any reminder the bridge has ever issued an id for.

They still **cannot** delete or edit a reminder, move one between lists, uncomplete one, or learn
any `calendarIdentifier`. A list is addressed by name, and a name that matches nothing — or that
matches two lists, which happens when two accounts both hold a "Reminders" — is refused rather than
guessed at, because guessing is how a write lands in somebody else's shared list.

**What it can also reach: every event in every calendar on this Mac — including reading them.**

This is the part to be clear-eyed about. The bridge holds **Calendars full access**, not write-only,
and that is not a convenience: **write-only access cannot enumerate calendars**, and enumerating
calendars is the only way to turn the string `"Privat"` into the calendar to write to. The
alternative was to address calendars by `calendarIdentifier` — an opaque handle a human would have
to copy out of the app once, that EventKit's own headers say is not sync-proof, and that would put
us straight back into the identifier-drift problem the reminder allowlist was removed to escape.
Phil chose the string, and therefore chose full access, with this paragraph as the price.

Concretely, anyone holding the bearer token can:

- read **every event in every calendar** for a window of up to 31 days ahead — title, notes, start,
  end, all-day flag, time zone — via `GET /v1/calendar-events` with no filter. That includes work
  meetings, medical appointments, and anything shared into this Mac's calendars by anyone else;
- create an event in any writable calendar they can name.

They **cannot** edit an event, delete one, read anything more than 31 days out, read the past,
enumerate attendees, or learn any `calendarIdentifier`. As with lists, a calendar is addressed by
name; a name matching nothing is `404 no_such_calendar` and a name matching two is
`409 ambiguous_calendar` — separate codes on purpose, because "check the spelling" and "you have
two calendars called that" are different problems with different fixes.

The two macOS grants are **independent**. Denying Calendars leaves the reminder routes working
untouched and vice versa; each half answers its own 503 (`reminders_unavailable` /
`calendar_unavailable`), which is what lets Erda tell Phil which System Settings row to open. If
reading events is not a trade you want, deny the Calendar prompt: the app stays useful for
reminders, and the setup window will say so in amber rather than claiming to be ready.

**Nothing about an event reaches the audit log.** The JSONL line for a calendar operation records
the calendar *name*, the operation, the result and the timing — never a title, never a note, and
**never a start or end time**. That last one is deliberate and load-bearing: an audit log that
recorded when Phil's appointments were would be a movement log. `AuditEvent` has no bare `String`
field and no `Date` field other than the request's own timestamp, so this is a property of the type
rather than of anyone's discipline (see [Hardening](#hardening-m7)).

**Four deliberate relaxations of the original spec** (all recorded as accepted-cost decisions, not
gaps that slipped through):

1. **Plain HTTP on the LAN, bearer token, instead of loopback-only + Tailscale Serve.** Both
   machines are on the same home Wi-Fi, and setting up Tailscale for this round was out of scope.
   **Accepted cost: the bearer token crosses the home Wi-Fi in cleartext** on every request.
   Anyone with access to that Wi-Fi (or anything upstream of it, e.g. a compromised router) can
   read the token off the wire and would then have the same access Erda has — create/list/complete
   on every reminder list, and create/read on every calendar — until the token is rotated. Note
   what that second half means for a passive eavesdropper: the **contents** of every calendar event
   in the next month cross the home Wi-Fi in cleartext every time Erda lists them. Mitigations in
   place: the token is only ever useful against this bridge's narrow API (no lateral capability, no
   delete, no edit) and rotation is cheap (see [Token rotation](#token-rotation)). The allowlist
   used to be listed here as a third mitigation bounding the blast radius; with it gone, the blast
   radius is every reminder list and every calendar — see above.
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
3. **No per-list allowlist**, as set out above: a user-approved widening of the boundary, taken with
   the reasoning written down rather than as a quiet simplification.
4. **Calendars full access rather than write-only.** Set out above: naming a calendar by its title
   requires enumerating calendars, which write-only forbids, so the only two options were "full
   access" or "address calendars by an opaque, non-sync-proof identifier." **Accepted cost: the
   bridge can read every calendar event on this Mac**, and so can anyone holding the token. Nothing
   compensates for this — it is the capability, not a side effect of one. What bounds it is the
   shape of the API (a 31-day forward window, no past, no edit, no delete) and the fact that the
   grant is separately deniable.

---

## Manual verification checklist

Everything here needs the app running with real Reminders and Calendar data and a real network —
none of it can be done from an agent session. Work through it once before relying on the bridge, and
again after any change to `BridgeEventKit`, the router, or the store.

> **Before the calendar rows:** make a throwaway calendar in Calendar.app and use it for every
> create below. **The bridge cannot delete an event**, so everything you create here you remove by
> hand afterwards.

**Permissions and revocation**

- [ ] Grant Reminders access → Setup window shows `full access`, non-zero list count.
- [ ] Revoke Reminders access in System Settings while the bridge is running → next request returns
      `503 reminders_unavailable`, no crash, no partial write.
- [ ] Re-grant → bridge recovers without a restart (confirm via `eventStoreChangedNotification`
      handling, not just a relaunch).
- [ ] Grant Calendar access → the prompt names **ErdaBridge**, quotes
      `NSCalendarsFullAccessUsageDescription`, and is the **full access** prompt (not write-only).
      Setup window shows `full access` with a non-zero calendar count.
- [ ] Revoke **Calendar** access only, leaving Reminders granted → calendar routes return
      `503 calendar_unavailable`; **the reminder routes keep working**. This is the one that would
      catch the two grants being wired together.
- [ ] Revoke **Reminders** access only, leaving Calendar granted → the mirror image: reminder routes
      503, calendar routes keep working.
- [ ] With Calendar denied, the readiness light is **amber, not green**, and says why.

**List addressing**

- [ ] `POST /v1/reminders` naming a real list by title creates the item, and it appears in that list
      in Reminders.app.
- [ ] The same with the title in the wrong case (`groceries` for `Groceries`) lands in the same
      list — a unique case-insensitive match is accepted.
- [ ] `POST` naming a list that does not exist → `404 no_such_list`, and **nothing is created
      anywhere**. Check the other lists, not just the response.
- [ ] `GET /v1/reminders` with no filter returns items from **every** list — the intended behaviour
      now, and worth seeing once against real data before relying on it.
- [ ] `GET /v1/reminders?list=<title>` narrows to that list; a title with a space or an umlaut works
      when percent-encoded (`?list=To%20Do`).
- [ ] Make two lists with the same title in two different accounts (e.g. iCloud and On My Mac), then
      address that title → `404 no_such_list`, **not** a write into either one. Delete one again
      afterwards.
- [ ] Add a read-only shared list, then `POST` to it → `409 list_read_only`, not a 500.
- [ ] Delete a list in Reminders.app that had bridge-created reminders in it, then try to complete
      one by id → `404`, not a silent success.
- [ ] Move a bridge-created reminder into another list, then complete it by id → succeeds, and
      completes it *in the list it is in now* (the bridge re-reads the reminder's current list
      rather than trusting the stored one).
- [ ] Complete an already-completed reminder → `200`, idempotent no-op, not an error.

**Calendar addressing and events**

- [ ] `POST /v1/calendar-events` naming a real writable calendar creates the event, and it appears
      in that calendar in Calendar.app at the **right wall-clock time** — not shifted by the UTC
      offset, and not as an all-day band.
- [ ] The same with the title in the wrong case (`privat` for `Privat`) lands in the same calendar,
      and the response echoes back `Privat`.
- [ ] `POST` naming a calendar that does not exist → `404 no_such_calendar`, and **nothing is
      created anywhere**. Check the other calendars, not just the response.
- [ ] Make two calendars with the same title in two different accounts (e.g. iCloud and On My Mac),
      then address that title → `409 ambiguous_calendar`, **not** a write into either one, and
      **not** a 404. Delete one again afterwards.
- [ ] `POST` to a subscribed or holiday calendar → `409 calendar_read_only`, not a 500.
- [ ] `GET /v1/calendar-events` with no filter returns events from **every** calendar — worth
      seeing once against real data before relying on it, since it is also the clearest look at what
      the [threat model](#threat-model) means in practice.
- [ ] `GET /v1/calendar-events?calendar=<title>` narrows to that calendar; a title with a space or
      an umlaut works when percent-encoded (`?calendar=Familie%20%2F%20Geteilt`).
- [ ] An event further out than `?days=` is **not** returned, and appears once the window widens.
- [ ] A real all-day event (a birthday) comes back with `isAllDay: true`, not as a timed event.
- [ ] A naive `startAt` (no offset), an `endAt` before the `startAt`, and an event longer than seven
      days each → `400 invalid_request`, with nothing created.
- [ ] `PUT`/`DELETE /v1/calendar-events` → `405` with `Allow: GET, POST`. There is no edit and no
      delete, and the response says so.
- [ ] Delete a bridge-created event in Calendar.app → it simply stops appearing; nothing 500s.

**Idempotency**

- [ ] Two concurrent `POST /v1/reminders` with the same `Idempotency-Key` and the same body →
      exactly one reminder created; the second response carries `Idempotency-Replayed: true`.
- [ ] Same key, different body → `409 idempotency_key_reuse`.
- [ ] Same key while the first request is still in flight → `409 request_in_progress`.
- [ ] The same three, for `POST /v1/calendar-events` — **exactly one event on the calendar** after a
      retried create. This matters more here than for reminders: a duplicate reminder is noise, a
      duplicate appointment is a double booking, and neither can be deleted through the bridge.

**Audit**

- [ ] `tail -f ~/Library/Logs/ErdaBridge/*.jsonl` while creating an event → the line carries
      `"op":"calendar.create"` and `"calendar":"<name>"`, and **no title, no notes, and no start or
      end time**.

**Token rotation**

- [ ] Rotate the token in the Setup window → the **old** token gets `401` on the very next request,
      no grace period.
- [ ] The new token works immediately without restarting the app.

**Network**

- [ ] `lsof -nP -iTCP:<port> -sTCP:LISTEN` shows a bind to the configured LAN address **only** — not
      `0.0.0.0`, not `127.0.0.1` (unless loopback was deliberately chosen for local-only testing).
- [ ] The port is unreachable from a host other than the one it's supposed to be reachable from
      (sanity-check that reachability is a bind choice, not a firewall rule this app doesn't have).
- [ ] Turn Wi-Fi off with the listener bound → status goes red within ~30s and starts retrying;
      turn it back on → rebinds without a relaunch.
- [ ] From `leela`: full status → create → list → complete round trip over the real LAN, and a
      calendar create → list round trip alongside it.

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
BridgeCore      pure logic, Sendable DTOs, no frameworks    ← RemindersService + CalendarService
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
- **Raw `import SQLite3`** (in the SDK, zero deps) for the id-map / idempotency store; a rotating
  **JSONL file** for the audit log so it stays `tail -f`-able and can't be locked out by a
  transaction.
- **EventKit behind an actor with a custom `DispatchSerialQueue` executor** — serialises mutations
  *and* keeps blocking `saveReminder:`/`saveEvent:`/`events(matching:)` calls off the cooperative
  pool. EventKit types are non-`Sendable` and must never cross an isolation boundary; `[EKReminder]`
  is mapped to DTOs inside the fetch completion closure, and `[EKEvent]` in the same expression that
  reads it.
- **One actor, one `EKEventStore`, for reminders *and* calendars** (`EventKitStore`, implementing
  both seams). Not a stylistic choice: the header asks for one store per process, objects fetched
  from one store cannot be used with another, and `EKEventStoreChanged` is a single notification
  covering both entity types — so a second store would be a second uncoordinated writer with its own
  `reset()` schedule. Sharing one store *between* two actors was the alternative and does not
  survive Swift 6: handing an `EKEventStore` to a second actor's initialiser is passing a
  non-`Sendable` class across an isolation boundary, which compiles only behind an
  `@unchecked Sendable` wrapper — a hole in the exact guarantee the module exists to keep. The two
  capabilities stay independent where it matters (authorization is read per entity type), and the
  request layer never learns they share an implementation: `BridgeServices` holds two protocol
  references that happen to point at the same instance.
- **Identifier drift is the main domain risk**, and it is why no list or calendar is stored by
  identifier any more. `EKCalendar.calendarIdentifier` and `EKCalendarItem.calendarItemIdentifier`
  are explicitly *not* sync-proof, so a list or calendar is resolved **by name against EventKit on
  every request** — which cannot go stale, and which is what made dropping the allowlist a
  simplification rather than a trade. It is also the reason calendars need *full* access rather than
  write-only: resolving a name means enumerating calendars. A name matching nothing, or matching
  two, is refused rather than guessed at — for calendars with two distinct codes
  (`no_such_calendar` / `ambiguous_calendar`), because Erda relays the reason to Phil and the two
  fixes differ; lists fold both into `no_such_list`, where the caller's next move is the same. The
  `reminder_map` table still keys on `calendarItemIdentifier` because a reminder has no other
  handle; a dangling id is always a `404`, and `complete` re-reads the reminder's *current* list
  before writing, so a re-homed or orphaned id can never quietly succeed.

### API

`GET /v1/status`, `POST /v1/reminders`, `GET /v1/reminders`, `POST /v1/reminders/{id}/complete`,
`POST /v1/calendar-events`, `GET /v1/calendar-events`. Bearer token on all six including status.

A list is named by its title: `{"list":"Groceries","title":"Buy milk"}` on create, and a repeatable
`?list=<percent-encoded title>` on list (omitted ⇒ every list). A calendar works the same way:
`{"calendar":"Privat","title":"Dentist","startAt":"…","endAt":"…"}` on create, and a repeatable
`?calendar=<percent-encoded title>` plus `?days=` (1–31, default 7) and `?limit=` (≤ 200, default
50) on list. The event window always starts *now* — "upcoming" is the only question the route
answers, and a caller-supplied start would need a zone to be meaningful and would let the route be
used to trawl history.

`GET /v1/status` answers
`{"availability":"ok","lists":["Groceries","Work"],"calendarAvailability":"ok","calendars":["Arbeit","Privat"]}`
— the names a caller may address, with the two capabilities reported **separately** because macOS
authorizes them separately. Both list routes keep their `{"items":[…]}` wrapper so they can gain a
field later. Query **values** are percent-decoded (titles have spaces in them); `+` is not a space,
and the **path** is still never decoded, so an encoded traversal stays a 404.

An event carries **no id** on the wire. No route takes one — there is no complete, no edit and no
delete — so an id would be a handle to nothing, and shipping one would imply an operation the bridge
does not have.

Both `startAt` and `endAt` must carry an explicit UTC offset (the same rule `dueAt` has), `endAt`
must be strictly after `startAt`, and an event may not exceed seven days. The optional `timeZone` is
a **canonical IANA identifier**: `Europe/Berlin` yes, `CEST` and `GMT+2` no — both of those parse
through `TimeZone(identifier:)` and both are ambiguous, so the check is membership of Foundation's
canonical zone list, not merely "it parsed". `UTC` is accepted and canonicalised to `GMT`.

Errors are `{"error":"<snake_code>","requestId":"…"}`
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
  substring. `AuditEvent` has no bare `String` field — the only things it takes from a request are
  the list and calendar *names*, and `ListName`/`CalendarName` cap those and refuse control
  characters — so this guards a structural guarantee rather than hunting for a leak, which is why
  it's cheap to run over adversarial input.
- `Tests/BridgeHTTPTests/CalendarResponderTests.swift` does the same for the calendar routes, with
  one extra assertion the reminder version does not need: the line must contain **no event time and
  no time zone**. `AuditEvent` has no `Date` field other than the request's own timestamp, so an
  appointment's time has nowhere to go — an audit log that recorded when Phil's appointments were
  would be a movement log.

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

**Lists and calendars are shown, not chosen.** The window lists every reminder list and every
calendar with its title, source and whether it is writable — read-only, because there is nothing to
pick. Its job is to show the exact spelling of each title, since that is what Erda sends, and (for
calendars) to make it obvious which ones are subscribed and therefore unwritable. The list picker,
alias assignment and broken-alias re-bind flows that used to live here went with the allowlist.

**Access is two buttons, not one.** Reminders and Calendar have separate grants, separate prompts
and separate status lines, because macOS records them separately. One button raising both prompts
would either ask for more than it says or fire two dialogs from one click.

**Readiness is a conjunction, with one graded exception.** The window says **Ready** only when
Reminders access is `full access`, Calendar access is `full access`, a token exists, and the
listener is bound to a non-loopback address. A Mac with no reminder lists or no calendars at all is
amber rather than green — there would be nothing to write to. Missing **Reminders** access is red;
missing **Calendar** access is amber, not red, and says so — a bridge with only reminders granted
genuinely serves half its routes, so calling that red would be as wrong as calling it green.
Anything less is amber or red with the specific reason.

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
