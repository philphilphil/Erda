# Browser Capability (agentic web + 1Password logins) — Design

- **Date:** 2026-06-04
- **Status:** Draft (design); pending user review → implementation plan
- **Author:** Phil + Erda (Claude)
- **Related:** [`2026-06-03-capabilities-overview-page-design.md`](2026-06-03-capabilities-overview-page-design.md) (the Capabilities page this extends with a live "Connected MCPs" panel); [`2026-05-30-whatsapp-bridge-and-error-scheduler-design.md`](2026-05-30-whatsapp-bridge-and-error-scheduler-design.md) (the bridge we extend for outbound images).

## Summary

Give Erda the ability to **use a real browser like a person** — navigate, read, click, type — and to
**log into specific websites** using credentials Erda never actually sees. The browsing is
**LLM-driven, not scripted**: the model decides each action from what the live page shows. A
"workflow" is a reusable **playbook prompt** ("log into Moxfield, open Collection, search `{card}`,
report the quantity"), not a hard-coded click-path.

The demo case: *"Do I own card X?"* → Erda opens moxfield.com (already logged in via a reused
session), searches the collection, interprets the result, and replies — optionally with a
**screenshot sent over WhatsApp**.

The design is **assembly, not invention**. The pieces are off-the-shelf:

- **Browser tools** come from the **Microsoft Playwright MCP** server (`@playwright/mcp`) — the
  generic `navigate / snapshot / click / type / screenshot` tool set. We do **not** write browser code.
- **Session persistence** is a built-in Playwright MCP feature (`--user-data-dir` / `--storage-state`
  on a volume) — login is rare; usually the agent is already logged in.
- **Secrets** live in **1Password**, fetched at runtime via the `op` CLI under a **scoped, read-only
  service account**. No passwords in Erda's DB; no custom crypto.

The only bespoke code is glue: the MCP-client wiring, a **browser sub-agent** exposed as one tool, a
small **secret-injection middleware** (resolve `op://` refs below the LLM + scrub logs), an
**outbound-image** path on the WhatsApp bridge, and a **live MCP panel** on the Capabilities page.

## Goals

- Erda can perform an **LLM-driven** web task end-to-end (the Moxfield collection check) and report
  the result in chat / over WhatsApp.
- Erda can **send a screenshot** over WhatsApp (a capability useful beyond browsing).
- Logins use **session reuse first**; a stored credential is a rare fallback.
- A credential's **plaintext value never enters the LLM context, the transcript, or Seq logs.**
- Secrets are scoped: a leaked token grants **read-only access to one dedicated 1Password vault**, nothing else.
- New web tasks are added by writing a **playbook prompt + an account binding** — no per-site code.
- The Capabilities page shows **which MCP servers are connected** and **which tools** they expose, live.
- Browsing runs on a **configurable model** (default = the orchestrator's), isolated so its big page
  snapshots don't pollute the main chat or its token budget.

## Non-goals (v1)

- **Scripted/deterministic site automation.** Explicitly rejected — the point is agentic browsing.
- **Fully automated first login / unattended 2FA solving.** First login is captured once manually
  (see *Session persistence*). 2FA/captcha on a *re-login* → Erda pings Phil to refresh the session.
- **A second agent runtime (browser-use / Python).** Considered and rejected (see *Alternatives*);
  Erda stays a single C#/MAF agent.
- **Generic multi-account credential UI with secret entry in the panel.** Secrets are managed in
  1Password; the panel only edits *non-secret* account metadata. (No write-back to 1Password.)
- **Domain-enforced secret binding at the framework level** (browser-use's `allowed_domains` gate).
  v1 scopes secrets per-workflow in Erda; framework-level domain enforcement is a later hardening.
- **Outbound video/document media** over WhatsApp — images only in v1.
- **Anti-bot / captcha evasion.** If a site blocks automation, the task fails loudly.

## Security posture

The new attack surface is "Erda can drive a browser and hold a key to one vault." Mitigations:

- **Secrets never reach the model.** The playbook and the LLM only ever see **1Password secret
  references** (`op://Erda/Moxfield/password`) — never values. A MAF function-invocation middleware
  resolves the reference *after* the model emits the `type` call and **scrubs the resolved value from
  the logged tool arguments** before the OTel/Seq span is written. Vision is disabled on login pages
  so a typed value can't leak via a screenshot either.
- **Least-privilege vault.** A dedicated **`Erda` vault** holds only Erda-usable logins. A
  **service account** is scoped `--vault Erda:read_items` (read-only, one vault). The
  `OP_SERVICE_ACCOUNT_TOKEN` is the single secret to guard; its blast radius is read-only access to
  that one vault, and it is rotatable in 1Password at any time.
- **Single-user, LAN-only trust boundary** (same as the rest of Erda). The panel that edits account
  *metadata* is the existing CSRF-guarded, optionally-password-gated LAN panel.
- **The agent can't plant credentials.** The agent has no tool to write the account registry or the
  1Password vault; Phil curates both. A prompt-injection can at most make the agent *attempt* a task
  with an already-configured account on an already-allowed playbook.
- **`OPENAI_API_KEY` stripping is unaffected.** Codex stays walled off and gets no browser; browsing
  is driven entirely by the Azure/Foundry-backed MAF agent.

> Honest scope: this protects against a stolen DB, a leaked transcript/log, and a leaked vault token.
> It does **not** defend against root on the Jetson (who can read the token from the process env) —
> the right altitude for a single-user home box.

---

## Architecture overview

```
WhatsApp / panel chat
        │
   erda agent  (orchestrator, gpt-chat-latest)
        │  tool: browse_web(task, account)
        ▼
   browser sub-agent  (own chat client; model = Erda:Browser:Deployment, default = orchestrator)
        │  MCP tools: navigate / snapshot / click / type / screenshot …
        │      ▲ secret-injection middleware: resolve op:// refs, scrub logs
        ▼      │
   Playwright MCP  (`npx @playwright/mcp`, stdio child process)
        │  --user-data-dir /data/browser   (persistent logged-in session)
        ▼
     Chromium (headless)
        │  screenshot → /media/<id>.png  (shared volume)
        ▼
   IWhatsAppSender.SendImageAsync → bridge POST /send-media → you
```

Two key isolation properties:

1. **The browser loop is a sub-agent exposed as one tool.** The orchestrator calls `browse_web(...)`
   and only the *final answer* returns. The dozens of large accessibility snapshots live in the
   sub-agent's short-lived conversation — out of the main chat history and the orchestrator's token budget.
2. **Secrets are resolved below the LLM** by middleware on the sub-agent's tool pipeline.

### Why in-image stdio (not a sidecar)

Phil confirmed Node + Playwright in the main image is acceptable. So Erda **spawns
`npx @playwright/mcp` as a stdio child process** (the same subprocess comfort Erda already has with
`codex`) and connects with the MCP C# SDK's stdio transport. This avoids a second container, an
inter-container port, and a shared-secret between them. A persistent profile is single-instance by
nature, which suits one stdio child.

**Rejected alternative:** a separate Playwright-MCP **sidecar** over HTTP/SSE (keeps the main image
lean, isolates crashes) — but it adds a container + network transport for no benefit given the
in-image constraint is already accepted. Keep it in mind if Chromium RAM on the Jetson becomes a problem.

---

## Component 1 — Playwright MCP in the image (Dockerfile + launch)

- **Dockerfile (runtime stage):** add Node, the `@playwright/mcp` package, and Chromium + its OS deps
  (`npx playwright install --with-deps chromium`). Mirrors the existing `codex` binary stage.
- **Launch (stdio):** Erda starts
  `npx @playwright/mcp@<pinned> --headless --user-data-dir /data/browser --isolated=false`
  (exact flags finalized at implementation; `--user-data-dir` gives the persistent profile). Pin the
  MCP version like `CODEX_VERSION` is pinned.
- **Profile volume:** a new `browser-data` named volume mounted at `/data/browser`, owned `1000:1000`
  (same chown note as `erda-data`). This is where the logged-in session lives.

## Component 2 — MCP client wiring (MAF)

Add the MCP C# SDK (`ModelContextProtocol` NuGet). A small `BrowserMcp` service owns the client
lifecycle:

```csharp
// Erda.Agents/Tools/BrowserMcp.cs  (names per the ModelContextProtocol SDK, confirmed at impl)
var transport = new StdioClientTransport(new()
{
    Command = "npx",
    Arguments = ["@playwright/mcp@<pinned>", "--headless", "--user-data-dir", "/data/browser"],
});
IMcpClient client = await McpClientFactory.CreateAsync(transport);
IList<McpClientTool> mcpTools = await client.ListToolsAsync();   // McpClientTool : AIFunction : AITool
```

`McpClientTool` derives from `AIFunction` → `AITool`, so the tools drop straight into the
`List<AITool>` MAF already builds in `ErdaAgent.Create`. (Verified seam: MAF's `AsAIAgent(tools: …)`
takes `IEnumerable<AITool>`.) The client is a singleton; the stdio child is launched once at startup
and disposed on shutdown.

> **Risk to confirm at implementation:** exact MCP SDK type/method names (`McpClientFactory`,
> `StdioClientTransport`, `ListToolsAsync`) and async-startup ordering with the agent build (today
> `ErdaAgent.Create` is synchronous). Likely fix: build the MCP tool list in an async hosted-service /
> `IAsyncInitializer` and inject it, or make agent registration async. This is the one piece needing a
> spike before the plan is final.

## Component 3 — Browser sub-agent + `browse_web` tool

Build a second `AIAgent` exactly like `ErdaAgent` but with its own deployment and the MCP tools, then
expose it as a single function tool (same pattern as `VoiceMemoWorkflow.CreateTool`):

```csharp
ChatClient browserChat = azureClient.GetChatClient(options.BrowserDeployment);   // defaults to ChatDeployment
AIAgent browser = browserChat
    .AsAIAgent(instructions: browserSystemPrompt, name: "browser", tools: mcpTools)
    .AsBuilder()
    .UseOpenTelemetry(ObservabilityOptions.ActivitySourceName, t => t.EnableSensitiveData = capture)
    .Use(SecretInjection.Middleware(secretResolver))   // Component 4 — wired in v1.1; omitted in v1 (no auto-login)
    .Use(ToolCallActivity.Middleware(recorder))
    .Build();

tools.Add(browser.AsAIFunction(
    name: "browse_web",
    description: "Perform a web task in a real browser (navigate, read, log in, interact) and return the result. " +
                 "Provide the task in plain language and, if a login is needed, the account key."));
```

The sub-agent's **system prompt** carries the generic browsing guidance (prefer accessibility
snapshots for acting; take a screenshot for the deliverable; if you land on a login page, fill it
using the provided `op://…` references; if blocked by 2FA/captcha, stop and say so). The **per-task
playbook** (e.g. Moxfield) is passed in as the task text, sourced from the prompt store.

## Component 4 — Secret-injection middleware (the important bit)

A MAF function-invocation middleware on the **browser sub-agent only**, modeled on the existing
`ToolCallActivity.Middleware`:

1. **Inspect** outgoing tool calls to the MCP `type`/`fill`-style tools.
2. If an argument contains a **1Password secret reference** (`op://…`), call
   `IOpSecretResolver.ResolveAsync(reference)` → the real value, and substitute it in the arguments
   that reach the browser.
3. **Scrub**: ensure the *recorded/telemetry* copy of the arguments keeps the `op://…` reference (or a
   `••••` mask), **never** the resolved value. This runs regardless of the `CaptureMessageContent` flag.

So the model emits `type(ref="op://Erda/Moxfield/password")`; the browser receives the real password;
the transcript and Seq only ever see the reference. 1Password's own reference syntax **is** the
placeholder — no custom scheme.

> **Risk to confirm:** that MAF's function-invocation middleware can mutate tool arguments *and* that
> the OTel span content is derived after our scrub (or that we can suppress content for these tools).
> If middleware can't cleanly mutate args, fallback is a thin proxy `AIFunction` wrapping each MCP
> `type` tool. Spike alongside Component 2.

## Component 5 — 1Password integration (`IOpSecretResolver`)

- **`op` CLI in the image:** add the `op` binary to the runtime stage (like `codex`).
- **Auth:** `OP_SERVICE_ACCOUNT_TOKEN` from `.env` → container env (compose). Scoped read-only to the
  `Erda` vault.
- **Resolver:** `OpSecretResolver : IOpSecretResolver` shells out to
  `op read "op://Erda/Moxfield/password"` (subprocess pattern like `CodexRunner`/`PreScriptRunner`),
  returns stdout. Cache per reference for the lifetime of a single `browse_web` call; never log the value.
- **No first-party .NET SDK** exists (official SDKs are Go/JS/Python), so the CLI subprocess is the
  correct .NET path and matches Erda's conventions.

## Component 6 — Account registry (DB, non-secret only)

New EF entity holding **only non-secret metadata** that binds a friendly account to its 1Password refs:

```csharp
// Erda.Core/Data/Entities/WebAccount.cs
public sealed class WebAccount
{
    public int Id { get; set; }
    public string Key { get; set; } = "";        // e.g. "moxfield" — referenced by playbooks
    public string Site { get; set; } = "";        // e.g. "moxfield.com"
    public string? LoginUrl { get; set; }
    public string UsernameRef { get; set; } = ""; // op://Erda/Moxfield/username
    public string PasswordRef { get; set; } = ""; // op://Erda/Moxfield/password
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- New `DbSet<WebAccount>` + migration `AddWebAccounts`.
- **No secret columns.** The references are not sensitive (they only name vault items).
- The browser sub-agent looks up the account by `Key` to obtain the refs to hand the LLM.

## Component 7 — Session persistence & first login

- **Reuse:** Playwright MCP `--user-data-dir /data/browser` keeps cookies/localStorage across restarts.
  The agent is normally already logged in and skips the password entirely.
- **First login (one-time, manual):** Phil logs into Moxfield headfully on his laptop and drops the
  exported session into the `browser-data` volume (or runs a one-off `op`-assisted login). Documented
  in the README; no in-app flow in v1 (the Jetson is headless).
- **Expiry:** if the agent lands on a login page → for plain user/pass sites the injection middleware
  fills it from 1Password automatically; for 2FA/captcha sites the sub-agent stops and Erda notifies
  Phil on WhatsApp to refresh the session.

## Component 8 — Screenshots → WhatsApp (bridge extension)

Independent, reusable capability. Today the bridge `/send` is text-only and `WhatsAppSender` only has
`SendAsync(text)`.

- **Bridge (Go):** add `POST /send-media` accepting `{ to, mediaPath, caption }`; read the file from
  the shared `/media` volume, `client.Upload(...)`, build an `ImageMessage`, send. Guard with the
  existing `X-Bridge-Secret`.
- **Erda (C#):** add `Task<bool> SendImageAsync(string toJid, string filePath, string? caption)` to
  `IWhatsAppSender` (+ the dev-prefix handling on the caption).
- **Hand-off:** the browser writes screenshots into `/media` (shared volume, both containers see it);
  the path goes to `/send-media`. Mirror of the inbound media flow, reversed.
- **Tool:** the browser sub-agent's `screenshot` (MCP) writes to `/media`; a small `send_image` notify
  tool (or the orchestrator's existing notify path) ships it. The agent decides when a screenshot adds value.

## Component 9 — Capabilities page: live "Connected MCPs"

`CapabilitiesView.vue` is currently a **static** hardcoded list. Add a live section:

- **API:** new `Api/Capabilities` group → `GET /api/capabilities/mcp` returning, per configured MCP
  server: `name`, `transport` (`stdio`), `status` (`connected`/`down`), and `tools` (names + short
  descriptions from `ListToolsAsync`). CSRF-exempt read like other GETs; auth like the rest of `/api`.
- **SPA:** a new "Connected MCPs" `Card` in `CapabilitiesView.vue` rendering server name + a
  `StatusBadge` + tool chips. Keep the existing static "Ask it to do" / "Runs on its own" cards;
  add a "Web browsing" entry to the static list too.
- Reusable for any future MCP, not just Playwright.

---

## Config

New keys (env-overridable, `Erda` section unless noted):

| Section | Key | Default | Purpose |
|---|---|---|---|
| `Erda` | `BrowserDeployment` | = `ChatDeployment` | Foundry deployment for the browser sub-agent. Bump to a stronger reasoning model if navigation is unreliable. |
| `Erda:Browser` | `Enabled` | `false` | Master switch; when off, `browse_web` and the MCP child are not started. |
| `Erda:Browser` | `McpCommand` / `McpArgs` | `npx` / `@playwright/mcp@<pinned> …` | How the MCP child is launched. |
| `Erda:Browser` | `UserDataDir` | `/data/browser` | Persistent profile path. |
| `Erda:Browser` | `OnePasswordVault` | `Erda` | Vault name used to validate `op://` refs. |
| `Erda:Browser` | `StepTimeout` / `MaxSteps` | `00:00:30` / `40` | Bound a runaway browser loop. |
| env | `OP_SERVICE_ACCOUNT_TOKEN` | — | Scoped read-only 1Password token (from `.env`). |

Model note: switching Erda's `ChatDeployment` to **`gpt-chat-latest`** (Phil's plan) is a separate
change; this design just inherits whatever it is and allows a browser-specific override. Prefer a
**pinned** Foundry deployment over the `-latest` alias in production.

---

## Staging

- **v1 — session-reuse only (leanest).** Components 1–3, 7 (manual first login), 8, 9. The agent
  reuses a captured session and **never types a password**, so the secret problem is entirely dodged.
  Delivers the Moxfield demo + screenshots-over-WhatsApp + the MCP panel end-to-end.
- **v1.1 — auto-login.** Add Components 4–6 (injection middleware, `op` resolver, account registry)
  so the agent can re-authenticate on expiry without Phil. This is where "save accounts" lands —
  in 1Password, with only refs in the DB.

Each stage is independently shippable and testable.

---

## Testing

**xUnit (`Erda.Tests`):**

- `OpSecretResolver` (with a fake/stub `op`): resolves a ref to a value; never includes the value in
  any thrown message; missing ref → actionable error.
- **Secret-injection middleware:** given a tool call with `type(ref="op://…")`, the *forwarded* args
  contain the resolved value and the *recorded* args contain only the reference/mask. A call with no
  `op://` ref passes through untouched. (Drive with a fake tool + fake resolver.)
- `WebAccount` round-trips through the store/migration; refs persist, no secret columns exist.
- `SendImageAsync` posts the expected `{to, mediaPath, caption}` to `/send-media` with the secret
  header (HttpClient test handler), and applies the dev caption prefix in Development.
- Capabilities endpoint maps a fake MCP tool list to the DTO (name/status/tools).
- The browser sub-agent is registered and `browse_web` appears in the orchestrator's tool list when
  `Erda:Browser:Enabled = true`, and is absent when disabled.

**Go (`whatsapp-bridge`):** `send-media` handler — rejects without the secret header; happy path
uploads + sends (mock the whatsmeow client like existing `send_test.go`).

**Manual / e2e:** with a captured Moxfield session, ask Erda "do I own <card>?" via the panel chat;
confirm it answers and (when asked) a screenshot arrives on WhatsApp; confirm Seq spans for the
`type` call show the `op://…` reference, **never** the password.

## Alternatives considered

- **Scripted per-site automation** — rejected; the explicit goal is agentic browsing that survives UI changes.
- **browser-use (Python) as a sidecar** — the most complete secret/2FA handling out of the box, but a
  second agent runtime + second model/key config in a deliberately single-agent C# system. Revisit only
  if multi-site, heavy-2FA logins become common.
- **OneCLI credential gateway** — network/HTTP-layer key injection; doesn't fit *browser form* logins
  (would require MITM-proxying Chromium's TLS). Good future fit if Erda grows **API-based** tools.
- **Roll-our-own DB secret store (Data Protection)** — viable, but 1Password is already Phil's vault,
  is more secure (scoped service account, rotation, audit), and removes custom crypto. Data Protection
  stays the fallback if 1Password is ever undesirable.
- **Playwright MCP `--secrets` for injection** — its secrets feature is *output redaction only* and
  documented as "not a security feature"; insufficient for secure form-fill, hence Component 4.

## Files touched (anticipated)

- `Dockerfile` — Node + `@playwright/mcp` + Chromium deps + `op` binary in the runtime stage.
- `docker-compose.yml` — `browser-data` volume; `OP_SERVICE_ACCOUNT_TOKEN`; `.env.example` additions.
- `Erda.Agents/Tools/BrowserMcp.cs` — MCP client lifecycle + tool list (new).
- `Erda.Agents/Tools/SecretInjection.cs` — function-invocation middleware (new).
- `Erda.Agents/Orchestration/ErdaAgent.cs` — build the browser sub-agent, add `browse_web` (gated by `Browser:Enabled`).
- `Erda.Core/Services/OpSecretResolver.cs` + `IOpSecretResolver.cs` — `op` subprocess (new).
- `Erda.Core/Data/Entities/WebAccount.cs` + `ErdaDbContext` DbSet + migration `AddWebAccounts`.
- `Erda.Core/Configuration/ErdaOptions.cs` (+ a new `BrowserOptions`) — config keys.
- `Erda.Core/ServiceCollectionExtensions.cs` — register resolver, MCP service, browser options.
- `Erda.Core/WhatsApp/WhatsAppSender.cs` — `SendImageAsync`.
- `whatsapp-bridge/send.go` (+ test) — `POST /send-media`.
- `Erda.Server/Api/Capabilities/*` — new endpoint group + DTOs; wired in `PanelApi`.
- `Erda.Server/Api/Accounts/*` — CRUD for `WebAccount` metadata (v1.1).
- `web/src/views/CapabilitiesView.vue` — "Connected MCPs" card + "Web browsing" static entry.
- `web/src/api/{client,types}.ts` — capabilities + accounts DTOs.
- `Erda.Tests/*` — resolver, middleware, store, sender, capabilities tests.
- `README.md` — first-login capture procedure; 1Password service-account setup.
