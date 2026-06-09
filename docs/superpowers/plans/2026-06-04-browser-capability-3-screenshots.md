# Browser Capability — Plan 3: Screenshots → WhatsApp (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Erda send an image (e.g. a browser screenshot) to Phil over WhatsApp. Add a `POST /send-media` endpoint to the Go bridge that uploads a file from the shared `/media` volume and sends it as an `ImageMessage`, an `IWhatsAppSender.SendImageAsync(toJid, filePath, caption)` on the Erda side, and a small `send_image` agent tool.

**Architecture:** This is the outbound mirror of the existing inbound image flow. The bridge already shares a `/media` volume with Erda and has `POST /send` for text guarded by `X-Bridge-Secret`. We add a sibling `POST /send-media` handler that validates the request, reads the file from within `MEDIA_DIR`, `client.Upload(...)`s it, builds a `waE2E.ImageMessage` from the upload response (per whatsmeow's documented pattern), and `client.SendMessage`s it. On the Erda side, `WhatsAppSender` gets `SendImageAsync` (POSTs `{to, mediaPath, caption}` with the secret header and the dev caption prefix), and `NotifyTools` gets a `send_image` tool so the agent can decide when a screenshot adds value. Browser screenshots already land in the shared volume via Plan 1's `--output-dir`, so this is purely the send path.

**Tech Stack:** Go (whatsmeow) for the bridge; .NET 10 + `Microsoft.Extensions.AI` for the Erda sender + tool.

**Spec:** [`../specs/2026-06-04-erda-browser-capability-design.md`](../specs/2026-06-04-erda-browser-capability-design.md) — this plan covers Component 8. It is independent of Plans 1–2 and can land any time.

**Scope boundary:** Images only (v1 non-goal: video/documents). The send path only; screenshots are already written to `OutputDir` (`/media` in prod) by the browser MCP. No changes to the inbound flow.

---

## Background facts (verified against the current branch — rely on these)

- **Bridge text send** (`whatsapp-bridge/send.go`): `newServer` builds a `mux` with `/healthz` and `/send`; `sendHandler` checks `secretEqual(req.Header.Get("X-Bridge-Secret"), cfg.SharedSecret)`, decodes JSON (1 MiB cap), parses the JID with `types.ParseJID`, builds `&waE2E.Message{Conversation: proto.String(body.Text)}`, and `client.SendMessage(ctx, to, msg)`. Reuse `secretEqual`.
- **Bridge media + upload:** `relay.go` downloads inbound media with `client.Download`. For outbound, whatsmeow's `Upload(ctx, plaintext []byte, whatsmeow.MediaImage)` returns `UploadResponse{ URL, DirectPath, MediaKey, FileEncSHA256, FileSHA256, FileLength uint64, … }`. The documented `ImageMessage` build (copied verbatim below) wires those into `waE2E.ImageMessage`. `cfg.MediaDir` (`MEDIA_DIR`, `/media` in compose) is the shared volume; `extensionForMime` already exists in `relay.go` (we add the inverse, ext→mime).
- **Bridge tests** (`send_test.go`) only unit-test `secretEqual` — the live `*whatsmeow.Client` is a concrete type with no test seam. So we **TDD the pure helpers and the request-rejection paths** (which return before touching the client, so a `nil` client is safe) and **verify the happy-path upload manually** (Task 4). This matches the existing test depth.
- **Erda sender** (`Erda.Core/WhatsApp/WhatsAppSender.cs`): `IWhatsAppSender` has only `SendAsync(toJid, text)`. It POSTs `{to, text}` to `{BridgeUrl}/send` with the `X-Bridge-Secret` header, prepends `DevOutboundPrefix` ("🧪 ") in Development, and returns a bool. Registered via `AddHttpClient<IWhatsAppSender, WhatsAppSender>()` in `Erda.Core/ServiceCollectionExtensions.cs`.
- **Notify tool** (`Erda.Agents/Tools/NotifyTools.cs`): `NotifyTools(IWhatsAppSender sender, IOptions<WhatsAppOptions> options)` exposes `AsTools()` → `[AIFunctionFactory.Create(MessageMe, "message_me")]`; resolves the owner JID via `WhatsAppJid.FromNumber(options.Value.OwnerNumber)`.
- **Test commands:** Go: `cd whatsapp-bridge && go test ./...`. .NET: `dotnet test Erda.Tests/Erda.Tests.csproj` (filter `--filter "FullyQualifiedName~ClassName"`). Keep the suite green.

---

## File Structure

**Modify (bridge, Go):**
- `whatsapp-bridge/send.go` — `sendMediaRequest`, `mediaTypeForExt`, `resolveMediaPath`, `buildImageMessage`, `sendMediaHandler`; register `/send-media` in `newServer`.
- `whatsapp-bridge/send_test.go` — tests for the pure helpers + handler rejection paths.

**Modify (Erda, C#):**
- `Erda.Core/WhatsApp/WhatsAppSender.cs` — add `SendImageAsync` to `IWhatsAppSender` + impl.
- `Erda.Agents/Tools/NotifyTools.cs` — add the `send_image` tool.

**Test (C#):**
- `Erda.Tests/WhatsAppSenderImageTests.cs`
- `Erda.Tests/NotifyToolsTests.cs`

---

## Task 1: Bridge `POST /send-media` (Go, TDD where testable)

**Files:**
- Modify: `whatsapp-bridge/send.go`
- Test: `whatsapp-bridge/send_test.go`

- [ ] **Step 1: Write the failing tests for the pure helpers + rejections**

Append to `whatsapp-bridge/send_test.go`:
```go
import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"go.mau.fi/whatsmeow"
)

func TestMediaTypeForExt(t *testing.T) {
	cases := map[string]struct {
		mime string
		ok   bool
	}{
		"/x/a.png":  {"image/png", true},
		"/x/a.PNG":  {"image/png", true},
		"/x/a.jpg":  {"image/jpeg", true},
		"/x/a.jpeg": {"image/jpeg", true},
		"/x/a.webp": {"image/webp", true},
		"/x/a.gif":  {"image/gif", true},
		"/x/a.txt":  {"", false},
		"/x/a":      {"", false},
	}
	for path, want := range cases {
		mime, ok := mediaTypeForExt(path)
		if mime != want.mime || ok != want.ok {
			t.Errorf("mediaTypeForExt(%q) = (%q,%v), want (%q,%v)", path, mime, ok, want.mime, want.ok)
		}
	}
}

func TestResolveMediaPath(t *testing.T) {
	dir := t.TempDir()
	good := filepath.Join(dir, "shot.png")

	if got, err := resolveMediaPath(dir, good); err != nil || got != good {
		t.Errorf("resolveMediaPath good = (%q,%v), want (%q,nil)", got, err, good)
	}
	// Traversal / outside-the-media-dir must be rejected.
	for _, bad := range []string{
		filepath.Join(dir, "..", "escape.png"),
		"/etc/passwd",
		"",
	} {
		if _, err := resolveMediaPath(dir, bad); err == nil {
			t.Errorf("resolveMediaPath(%q) expected error, got nil", bad)
		}
	}
}

func TestBuildImageMessage(t *testing.T) {
	up := whatsmeow.UploadResponse{
		URL: "https://mmg.whatsapp.net/x", DirectPath: "/v/x",
		MediaKey: []byte{1}, FileEncSHA256: []byte{2}, FileSHA256: []byte{3}, FileLength: 42,
	}
	msg := buildImageMessage(up, "image/png", "hello")
	if msg.GetURL() != up.URL || msg.GetDirectPath() != up.DirectPath {
		t.Errorf("url/directpath not copied: %+v", msg)
	}
	if msg.GetMimetype() != "image/png" || msg.GetCaption() != "hello" || msg.GetFileLength() != 42 {
		t.Errorf("mime/caption/len wrong: %+v", msg)
	}
	// Empty caption => no caption field set.
	if buildImageMessage(up, "image/png", "").Caption != nil {
		t.Error("empty caption should leave Caption nil")
	}
}

// The handler must reject bad requests BEFORE touching the (nil) client.
func TestSendMediaHandlerRejections(t *testing.T) {
	dir := t.TempDir()
	cfg := Config{SharedSecret: "s3cret", MediaDir: dir}
	h := sendMediaHandler(cfg, nil) // nil client: rejections return before any client call

	do := func(method, secret, body string) *httptest.ResponseRecorder {
		req := httptest.NewRequest(method, "/send-media", strings.NewReader(body))
		if secret != "" {
			req.Header.Set("X-Bridge-Secret", secret)
		}
		rr := httptest.NewRecorder()
		h(rr, req)
		return rr
	}

	missing := filepath.Join(dir, "nope.png")
	tests := []struct {
		name, method, secret, body string
		want                       int
	}{
		{"wrong method", http.MethodGet, "s3cret", "", http.StatusMethodNotAllowed},
		{"bad secret", http.MethodPost, "wrong", `{}`, http.StatusUnauthorized},
		{"no secret", http.MethodPost, "", `{}`, http.StatusUnauthorized},
		{"bad json", http.MethodPost, "s3cret", `{`, http.StatusBadRequest},
		{"missing fields", http.MethodPost, "s3cret", `{"to":""}`, http.StatusBadRequest},
		{"non-image ext", http.MethodPost, "s3cret", `{"to":"1@s.whatsapp.net","mediaPath":"` + filepath.Join(dir, "a.txt") + `"}`, http.StatusBadRequest},
		{"missing file", http.MethodPost, "s3cret", `{"to":"1@s.whatsapp.net","mediaPath":"` + missing + `"}`, http.StatusBadRequest},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := do(tt.method, tt.secret, tt.body).Code; got != tt.want {
				t.Errorf("status = %d, want %d", got, tt.want)
			}
		})
	}
}
```
> If `send_test.go` has no `import` block yet (it currently imports only `"testing"`), merge these imports into a single block — Go allows one `import (...)` per file. Replace the existing `import "testing"` with the block above.

- [ ] **Step 2: Run to verify it fails**

Run: `cd whatsapp-bridge && go test ./...`
Expected: FAIL — `mediaTypeForExt`, `resolveMediaPath`, `buildImageMessage`, `sendMediaHandler` undefined.

- [ ] **Step 3: Implement the handler + helpers**

In `whatsapp-bridge/send.go`, add `"os"`, `"path/filepath"` to the import block, and `"go.mau.fi/whatsmeow"` is already imported. Then:

Register the route in `newServer` (after the `/send` line):
```go
	mux.HandleFunc("/send-media", sendMediaHandler(cfg, client))
```
Update `newServer`'s doc comment route list to mention `POST /send-media`.

Add the request type near `sendRequest`:
```go
// sendMediaRequest is the body of POST /send-media. mediaPath must point at a file inside MEDIA_DIR
// (the volume shared with Erda); caption is optional.
type sendMediaRequest struct {
	To        string `json:"to"`        // destination JID
	MediaPath string `json:"mediaPath"` // absolute path inside MEDIA_DIR
	Caption   string `json:"caption"`   // optional caption
}
```

Add the handler + helpers (e.g. after `sendHandler`):
```go
// sendMediaHandler returns the handler for POST /send-media: it reads an image from the shared media
// volume, uploads it to WhatsApp, and sends it as an ImageMessage. Mirror of the inbound media flow.
func sendMediaHandler(cfg Config, client *whatsmeow.Client) http.HandlerFunc {
	return func(w http.ResponseWriter, req *http.Request) {
		if req.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		if !secretEqual(req.Header.Get("X-Bridge-Secret"), cfg.SharedSecret) {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}

		var body sendMediaRequest
		dec := json.NewDecoder(http.MaxBytesReader(w, req.Body, 1<<20))
		if err := dec.Decode(&body); err != nil {
			http.Error(w, "invalid JSON body", http.StatusBadRequest)
			return
		}
		if strings.TrimSpace(body.To) == "" || strings.TrimSpace(body.MediaPath) == "" {
			http.Error(w, "fields 'to' and 'mediaPath' are required", http.StatusBadRequest)
			return
		}

		to, err := types.ParseJID(body.To)
		if err != nil {
			http.Error(w, "invalid 'to' JID: "+err.Error(), http.StatusBadRequest)
			return
		}

		mime, ok := mediaTypeForExt(body.MediaPath)
		if !ok {
			http.Error(w, "unsupported media type (images only: png/jpg/webp/gif)", http.StatusBadRequest)
			return
		}

		path, err := resolveMediaPath(cfg.MediaDir, body.MediaPath)
		if err != nil {
			http.Error(w, "invalid mediaPath: "+err.Error(), http.StatusBadRequest)
			return
		}
		data, err := os.ReadFile(path)
		if err != nil {
			http.Error(w, "could not read media file: "+err.Error(), http.StatusBadRequest)
			return
		}

		uploaded, err := client.Upload(req.Context(), data, whatsmeow.MediaImage)
		if err != nil {
			slog.Warn("media upload failed", "to", to.String(), "error", err)
			http.Error(w, "upload failed: "+err.Error(), http.StatusInternalServerError)
			return
		}

		msg := &waE2E.Message{ImageMessage: buildImageMessage(uploaded, mime, body.Caption)}
		resp, err := client.SendMessage(req.Context(), to, msg)
		if err != nil {
			slog.Warn("send media failed", "to", to.String(), "error", err)
			http.Error(w, "send failed: "+err.Error(), http.StatusInternalServerError)
			return
		}

		slog.Info("outbound media sent", "to", to.String(), "id", resp.ID, "bytes", len(data), "mime", mime)
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	}
}

// buildImageMessage copies an upload response into a protobuf ImageMessage (per whatsmeow's docs).
func buildImageMessage(up whatsmeow.UploadResponse, mime, caption string) *waE2E.ImageMessage {
	msg := &waE2E.ImageMessage{
		Mimetype:      proto.String(mime),
		URL:           &up.URL,
		DirectPath:    &up.DirectPath,
		MediaKey:      up.MediaKey,
		FileEncSHA256: up.FileEncSHA256,
		FileSHA256:    up.FileSHA256,
		FileLength:    &up.FileLength,
	}
	if caption != "" {
		msg.Caption = proto.String(caption)
	}
	return msg
}

// mediaTypeForExt maps a file path's extension to an image mimetype. Images only in v1.
func mediaTypeForExt(path string) (string, bool) {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".png":
		return "image/png", true
	case ".jpg", ".jpeg":
		return "image/jpeg", true
	case ".webp":
		return "image/webp", true
	case ".gif":
		return "image/gif", true
	default:
		return "", false
	}
}

// resolveMediaPath cleans mediaPath and ensures it stays inside mediaDir (no traversal / absolute
// escapes), returning the cleaned absolute path. Both the bridge and Erda share this volume.
func resolveMediaPath(mediaDir, mediaPath string) (string, error) {
	if strings.TrimSpace(mediaPath) == "" {
		return "", fmt.Errorf("empty path")
	}
	cleanDir := filepath.Clean(mediaDir)
	clean := filepath.Clean(mediaPath)
	if clean != cleanDir && !strings.HasPrefix(clean, cleanDir+string(os.PathSeparator)) {
		return "", fmt.Errorf("path is outside the media directory")
	}
	return clean, nil
}
```
> `fmt` is already imported in `send.go`? It is not in the current file — add `"fmt"` to the import block. `proto`, `waE2E`, `types`, `slog`, `strings` are already imported.

- [ ] **Step 4: Run the tests + vet**

Run: `cd whatsapp-bridge && go vet ./... && go test ./...`
Expected: PASS (the new helper/rejection tests + the existing `TestSecretEqual`).

- [ ] **Step 5: Commit**
```bash
git add whatsapp-bridge/send.go whatsapp-bridge/send_test.go
git commit -m "feat(bridge): POST /send-media — upload + send an image (guarded, media-dir scoped)"
```

---

## Task 2: `IWhatsAppSender.SendImageAsync` (C#, TDD)

**Files:**
- Modify: `Erda.Core/WhatsApp/WhatsAppSender.cs`
- Test: `Erda.Tests/WhatsAppSenderImageTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Erda.Tests/WhatsAppSenderImageTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class WhatsAppSenderImageTests
{
    /// <summary>Captures the outbound request and returns a canned status.</summary>
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class FakeEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Erda.Tests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static WhatsAppSender Make(CapturingHandler handler, string env)
    {
        var opts = Options.Create(new WhatsAppOptions { BridgeUrl = "http://bridge:8088", SharedSecret = "s3cret" });
        return new WhatsAppSender(new HttpClient(handler), opts, new FakeEnv(env), NullLogger<WhatsAppSender>.Instance);
    }

    [Fact]
    public async Task Posts_to_send_media_with_secret_and_fields()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var ok = await Make(handler, Environments.Production)
            .SendImageAsync("4915123456789@s.whatsapp.net", "/media/shot.png", "the page");

        Assert.True(ok);
        Assert.EndsWith("/send-media", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("s3cret", handler.Request.Headers.GetValues("X-Bridge-Secret").Single());

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("4915123456789@s.whatsapp.net", doc.RootElement.GetProperty("to").GetString());
        Assert.Equal("/media/shot.png", doc.RootElement.GetProperty("mediaPath").GetString());
        Assert.Equal("the page", doc.RootElement.GetProperty("caption").GetString());
    }

    [Fact]
    public async Task Prefixes_the_caption_in_development()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        await Make(handler, Environments.Development)
            .SendImageAsync("1@s.whatsapp.net", "/media/shot.png", "hi");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.StartsWith("🧪", doc.RootElement.GetProperty("caption").GetString());
    }

    [Fact]
    public async Task Returns_false_on_a_non_success_status()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var ok = await Make(handler, Environments.Production)
            .SendImageAsync("1@s.whatsapp.net", "/media/shot.png", null);
        Assert.False(ok);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~WhatsAppSenderImageTests"`
Expected: FAIL — `SendImageAsync` not on `IWhatsAppSender`.

- [ ] **Step 3: Implement `SendImageAsync`**

In `Erda.Core/WhatsApp/WhatsAppSender.cs`, add to the `IWhatsAppSender` interface:
```csharp
    /// <summary>Sends an image file (read by the bridge from the shared media volume) to a JID,
    /// with an optional caption. Returns whether the bridge accepted it.</summary>
    Task<bool> SendImageAsync(string toJid, string filePath, string? caption, CancellationToken cancellationToken = default);
```
And add the implementation to the `WhatsAppSender` class (after `SendAsync`):
```csharp
    public async Task<bool> SendImageAsync(string toJid, string filePath, string? caption, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BridgeUrl))
        {
            logger.LogWarning("WhatsApp bridge URL is not configured; cannot send image.");
            return false;
        }

        // Same dev tagging as text: distinguish a dev instance's images from prod's.
        if (hostEnvironment.IsDevelopment())
            caption = DevOutboundPrefix + (caption ?? "");

        var url = $"{o.BridgeUrl.TrimEnd('/')}/send-media";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { to = toJid, mediaPath = filePath, caption }),
        };
        request.Headers.TryAddWithoutValidation("X-Bridge-Secret", o.SharedSecret);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Bridge /send-media returned {Status} when sending to {To}.", (int)response.StatusCode, toJid);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to POST image to the WhatsApp bridge at {Url}.", url);
            return false;
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~WhatsAppSenderImageTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**
```bash
git add Erda.Core/WhatsApp/WhatsAppSender.cs Erda.Tests/WhatsAppSenderImageTests.cs
git commit -m "feat(whatsapp): IWhatsAppSender.SendImageAsync -> bridge /send-media"
```

---

## Task 3: `send_image` agent tool (C#, TDD)

**Files:**
- Modify: `Erda.Agents/Tools/NotifyTools.cs`
- Test: `Erda.Tests/NotifyToolsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Erda.Tests/NotifyToolsTests.cs`:
```csharp
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.WhatsApp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class NotifyToolsTests
{
    private sealed class FakeSender : IWhatsAppSender
    {
        public (string Jid, string Path, string? Caption)? ImageCall { get; private set; }
        public Task<bool> SendAsync(string toJid, string text, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendImageAsync(string toJid, string filePath, string? caption, CancellationToken ct = default)
        {
            ImageCall = (toJid, filePath, caption);
            return Task.FromResult(true);
        }
    }

    private static NotifyTools Make(FakeSender sender) =>
        new(sender, Options.Create(new WhatsAppOptions { OwnerNumber = "+4915123456789" }));

    private static AIFunction Tool(NotifyTools tools, string name) =>
        (AIFunction)tools.AsTools().Single(t => ((AIFunction)t).Name == name);

    [Fact]
    public void Exposes_message_me_and_send_image()
    {
        var names = Make(new FakeSender()).AsTools().Select(t => ((AIFunction)t).Name).ToList();
        Assert.Contains("message_me", names);
        Assert.Contains("send_image", names);
    }

    [Fact]
    public async Task Send_image_sends_an_existing_file_to_the_owner()
    {
        var sender = new FakeSender();
        var file = Path.GetTempFileName();
        try
        {
            var result = (string)(await Tool(Make(sender), "send_image")
                .InvokeAsync(new() { ["filePath"] = file, ["caption"] = "shot" }))!;

            Assert.Contains("delivered", result, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(sender.ImageCall);
            Assert.Equal(file, sender.ImageCall!.Value.Path);
            Assert.Equal("shot", sender.ImageCall.Value.Caption);
            Assert.Equal("4915123456789@s.whatsapp.net", sender.ImageCall.Value.Jid);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Send_image_refuses_a_missing_file()
    {
        var sender = new FakeSender();
        var result = (string)(await Tool(Make(sender), "send_image")
            .InvokeAsync(new() { ["filePath"] = "/no/such/file.png" }))!;

        Assert.Contains("Cannot send", result);
        Assert.Null(sender.ImageCall);   // never reached the sender
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~NotifyToolsTests"`
Expected: FAIL — `send_image` not exposed.

- [ ] **Step 3: Add the `send_image` tool**

In `Erda.Agents/Tools/NotifyTools.cs`, add `using System.IO;` is already covered by implicit usings; add the tool to `AsTools()`:
```csharp
    public IList<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(MessageMe, "message_me"),
        AIFunctionFactory.Create(SendImage, "send_image"),
    ];
```
and add the method (after `MessageMe`):
```csharp
    [Description(
        "Send an image file to Phil (the owner) on WhatsApp — e.g. a screenshot the browser captured. " +
        "Provide the absolute file path (the browser writes screenshots to the media directory) and an " +
        "optional caption. Returns whether it was delivered.")]
    private async Task<string> SendImage(
        [Description("Absolute path to the image file to send.")] string filePath,
        [Description("Optional caption to send with the image.")] string? caption = null)
    {
        var jid = WhatsAppJid.FromNumber(options.Value.OwnerNumber);
        if (string.IsNullOrEmpty(jid))
            return "Cannot send: the WhatsApp owner number is not configured.";
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return $"Cannot send: there is no file at '{filePath}'.";

        var ok = await sender.SendImageAsync(jid, filePath, caption);
        return ok ? "Image delivered to Phil on WhatsApp." : "Failed to send the image (the WhatsApp bridge may be down).";
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Erda.Tests/Erda.Tests.csproj --filter "FullyQualifiedName~NotifyToolsTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the whole suite**

Run: `dotnet build Erda.slnx && dotnet test Erda.Tests/Erda.Tests.csproj`
Expected: green.

- [ ] **Step 6: Commit**
```bash
git add Erda.Agents/Tools/NotifyTools.cs Erda.Tests/NotifyToolsTests.cs
git commit -m "feat(agent): send_image tool — ship a screenshot to Phil on WhatsApp"
```

---

## Task 4: End-to-end verification (manual)

**No code — proves the upload+send path the unit tests can't.**

- [ ] **Step 1: Run the bridge + Erda sharing a media dir**

On the deployed stack (`media:/media` is already shared) or locally with the bridge running and `MEDIA_DIR` pointing at the same folder Erda's `Erda:Browser:OutputDir` uses, drop a PNG into that folder (e.g. `/media/test.png`).

- [ ] **Step 2: Send it via the agent**

Via the panel chat (or WhatsApp): *"Send me the screenshot at /media/test.png with caption 'hello'."*
Expected: Erda calls `send_image`; the image arrives on WhatsApp with the caption. In Development the caption is prefixed with 🧪.

- [ ] **Step 3: Send it via the browser path (integration with Plan 1/2)**

Ask Erda to browse a page, take a screenshot, and send it: *"Open example.com, screenshot it, and send me the shot."*
Expected: the browser MCP writes the screenshot into `OutputDir` (= `/media`), and `send_image` delivers it. Confirm the `tool_call` entries for `browse_web` and `send_image` in the Activity feed.

- [ ] **Step 4: Verify the guard**

`curl -X POST -H "X-Bridge-Secret: wrong" http://<bridge>:8088/send-media -d '{}'` → 401.
`curl -X POST -H "X-Bridge-Secret: <secret>" .../send-media -d '{"to":"1@s.whatsapp.net","mediaPath":"/etc/passwd"}'` → 400 (outside media dir / non-image).

- [ ] **Step 5: Final commit (if any tweaks were needed)**
```bash
git add -A && git commit -m "chore(browser): plan-3 verification fixups"
```

---

## Self-Review

**Spec coverage (Component 8):**
- Bridge `POST /send-media` (`{to, mediaPath, caption}`, reads from `/media`, `client.Upload` + `ImageMessage`, `X-Bridge-Secret` guard) → Task 1. ✓
- `IWhatsAppSender.SendImageAsync(toJid, filePath, caption)` + dev caption prefix → Task 2. ✓
- Hand-off via the shared `/media` volume (browser writes the screenshot; the path goes to `/send-media`) → already wired by Plan 1's `OutputDir`; exercised in Task 4 Step 3. ✓
- A small `send_image` notify tool so the agent decides when a screenshot adds value → Task 3. ✓

**Spec Testing section, each has a test or step:**
- Go `send-media` handler rejects without the secret header → `TestSendMediaHandlerRejections` ("bad secret"/"no secret"). ✓ Happy-path upload+send → Task 4 (the live `*whatsmeow.Client` has no unit seam; pure helpers `buildImageMessage`/`mediaTypeForExt`/`resolveMediaPath` are unit-tested instead). ✓
- `SendImageAsync` posts the expected `{to, mediaPath, caption}` with the secret header and applies the dev caption prefix → `WhatsAppSenderImageTests`. ✓

**Placeholder scan:** none — every step has full code. The only judgement call (the `nil`-client handler test) is justified inline: rejections return before any client call.

**Type consistency:** `sendMediaRequest{To,MediaPath,Caption}` (Go) matches the C# `{to, mediaPath, caption}` JSON body. `buildImageMessage(whatsmeow.UploadResponse, string, string)` uses the verified `UploadResponse` field names (`URL/DirectPath/MediaKey/FileEncSHA256/FileSHA256/FileLength`). `IWhatsAppSender.SendImageAsync(string,string,string?,CancellationToken)` is implemented in `WhatsAppSender`, faked in both `NotifyToolsTests` and (text-only) elsewhere, and called by the `send_image` tool. `NotifyTools.AsTools()` exposes `message_me` + `send_image`.

> **Note on the existing `IWhatsAppSender` fakes:** adding `SendImageAsync` to the interface means any existing test double implementing `IWhatsAppSender` must add the new member. Search before building: `grep -rn "IWhatsAppSender" Erda.Tests` — update each fake with a `SendImageAsync` returning `Task.FromResult(true)` (or `throw new NotImplementedException()` if that double is never used for images). This is the one ripple from widening the interface.
