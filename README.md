# Erda — a personal agent on the Microsoft Agent Framework (.NET) with DevUI

Erda is a lean MVP personal assistant built on the **Microsoft Agent Framework (MAF)** for
.NET, with MAF's browser-based **DevUI** as the interaction surface. It does three things:

1. **Chat** — talk to Erda in DevUI.
2. **Browse + edit an Obsidian vault** — list, read, search, write, and append notes.
3. **Voice-memo pipeline** — an Apple Voice Memo `.m4a` → OpenAI speech-to-text → a **Codex**
   agent (your ChatGPT subscription) cleans it up → the result is written into your vault.
   This is modeled as a MAF **workflow**.

## The three-credential model (the whole point of the design)

Erda deliberately uses **three separate credential contexts**. Keeping them apart is the
reason this project exists:

| Capability | Runs on | Auth | Credential |
|---|---|---|---|
| **Chat agent** (`gpt-5-mini`) | Azure AI Foundry, via the Azure OpenAI client | API key | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` |
| **Transcription** (`gpt-4o-transcribe`) | OpenAI platform | API key (pay-per-token) | `OPENAI_API_KEY` |
| **Codex** (`gpt-5-codex`) | ChatGPT **subscription**, via the `codex` CLI | logged-in session in `~/.codex` | *(none in this app)* |

> **Hard rule, enforced in code:** `OPENAI_API_KEY` is **stripped** from the Codex subprocess
> environment (`ProcessStartInfo.Environment.Remove("OPENAI_API_KEY")` in
> [`Services/CodexRunner.cs`](Services/CodexRunner.cs)). This forces Codex to authenticate with
> the ChatGPT subscription instead of falling back to per-token API billing. On every launch
> Erda logs the command and `OPENAI_API_KEY stripped from child env: True`.

The Azure key and the OpenAI-platform key are **different keys** — don't conflate them.

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` → `10.0.x`).
- **`codex` CLI** installed and logged in via your ChatGPT subscription (`codex --version`;
  auth lives in `~/.codex`). Verify with `codex exec -m gpt-5-codex "hello"`.
- A **gpt-5-mini deployment in Azure AI Foundry**, and the endpoint + key from the portal.
- An **OpenAI platform API key** (only used for transcription).
- An **Obsidian vault** to point at (defaults to `/Users/phil/TestingNotes`).

### Set the environment variables

These are read at runtime (not committed anywhere). Export them in the shell you run from:

```bash
export AZURE_OPENAI_ENDPOINT="https://<your-foundry-resource>.openai.azure.com/"
export AZURE_OPENAI_API_KEY="<your-foundry-key>"
export OPENAI_API_KEY="sk-...your-openai-platform-key..."
```

Erda starts even if these are unset (so DevUI loads), but the relevant capability will fail
with a clear message until the key is present. The startup log prints which are `set` / `MISSING`.

## Run

```bash
cd Erda
dotnet run
```

Then open the DevUI URL printed in the console, e.g. **`http://localhost:5167/devui`**
(the exact port comes from `Properties/launchSettings.json`; `/` redirects to `/devui`).

DevUI is only mounted in the **Development** environment (it exposes system prompts), guarded by
`app.Environment.IsDevelopment()`.

## Using it in DevUI

- **Chat** — select **`erda`** and talk to it.
- **Vault tools** — ask Erda to `list_notes`, `read_note`, `search_notes`, `write_note`, or
  `append_note`. All paths are confined to the vault root; anything that escapes is rejected.
- **Voice memo** — select the **`voice-memo`** entity and give it the **absolute path** to a
  real `.m4a` file. Erda transcribes it, sends the transcript to Codex, and writes the
  resulting Markdown note to `VoiceMemos/<yyyy-MM-dd-HHmmss>.md` in your vault.
  You can also trigger the same pipeline conversationally by asking Erda to
  `process_voice_memo` with a path.

## Configuration

`appsettings.json` (`Erda` section) plus the three environment variables above.

| Setting | Default | Purpose |
|---|---|---|
| `Erda:ChatDeployment` | `gpt-5-mini` | Foundry deployment name for the chat model |
| `Erda:TranscribeModel` | `gpt-4o-transcribe` | OpenAI STT model (`gpt-4o-mini-transcribe` is cheaper) |
| `Erda:CodexModel` | `gpt-5-codex` | Model passed to `codex exec -m` |
| `Erda:CodexReasoningEffort` | `high` | `codex exec -c model_reasoning_effort` |
| `Erda:VaultPath` | `/Users/phil/TestingNotes` | Obsidian vault root Erda may read/write |
| `Erda:VoiceMemoSubfolder` | `VoiceMemos` | Where processed memos are saved |

### Point it at a different vault

Edit `Erda:VaultPath` in `appsettings.json`, or override without editing files:

```bash
Erda__VaultPath="/Users/you/MyVault" dotnet run
```

(The double underscore is the .NET convention for nesting config sections in env vars.)

## Project layout

```
Erda/
  Program.cs                          # host + DI + DevUI wiring
  Configuration/ErdaOptions.cs        # strongly-typed settings
  Agents/ErdaAgent.cs                 # chat agent factory + instructions + tools
  Tools/ObsidianTools.cs             # the 5 vault function tools
  Services/VaultService.cs            # path-safe file IO under VaultPath
  Services/Transcriber.cs             # OpenAI audio transcription (OPENAI_API_KEY)
  Services/CodexRunner.cs             # codex exec wrapper; strips OPENAI_API_KEY
  Workflows/VoiceMemoWorkflow.cs      # the 3-step workflow + shared note writer
  Workflows/Executors/                # TranscribeExecutor, CodexExecutor, ObsidianWriteExecutor
```

## Notes on the MAF API (verified against the installed packages)

MAF is in active preview; a few names differ from older docs/samples. As built here against the
1.8.0 train (May 2026):

- The chat agent uses `new AzureOpenAIClient(uri, new ApiKeyCredential(key)).GetChatClient(deployment).AsAIAgent(...)`.
  Azure.AI.OpenAI 2.x uses **`System.ClientModel.ApiKeyCredential`**, not `Azure.AzureKeyCredential`.
- DevUI transport is registered on the **builder**: `builder.AddOpenAIResponses()` /
  `builder.AddOpenAIConversations()` (extensions on `IHostApplicationBuilder`), then
  `app.MapOpenAIResponses()` / `app.MapOpenAIConversations()` / `app.MapDevUI()`.
- The workflow is registered with `builder.AddWorkflow("voice-memo", factory).AddAsAIAgent()` —
  `AddAsAIAgent()` is what makes a workflow runnable as a selectable entity in DevUI.
- A pure-code executor workflow (no `AIAgent` nodes) takes a `string`-typed start executor that
  receives the user's input text directly — no `ChatMessage` adapter or `TurnToken` needed.
- **An agent's `name` must equal its registration key** (e.g. both `"erda"`), or DevUI's eager
  entity enumeration throws at startup.

## Package versions

`Microsoft.Agents.AI` / `.OpenAI` / `.Workflows` `1.8.0` (stable);
`Microsoft.Agents.AI.Hosting` `1.8.0-preview`, `.Hosting.OpenAI` `1.8.0-alpha`,
`.DevUI` `1.8.0-preview`; `Microsoft.Extensions.AI` `10.6.0`;
`Azure.AI.OpenAI` `2.9.0-beta.1`; `OpenAI` `2.10.0`. See `Erda.csproj`.
