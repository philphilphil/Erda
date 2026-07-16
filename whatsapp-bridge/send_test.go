package main

import (
	"bytes"
	"image"
	"image/png"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"

	"go.mau.fi/whatsmeow"
)

// secretEqual guards POST /send, so verify it accepts only an exact match.
func TestSecretEqual(t *testing.T) {
	tests := []struct {
		name      string
		got, want string
		equal     bool
	}{
		{"exact match", "s3cret", "s3cret", true},
		{"mismatch", "wrong", "s3cret", false},
		{"different length", "s3cre", "s3cret", false},
		{"empty got", "", "s3cret", false},
		{"both empty", "", "", true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := secretEqual(tt.got, tt.want); got != tt.equal {
				t.Errorf("secretEqual(%q, %q) = %v, want %v", tt.got, tt.want, got, tt.equal)
			}
		})
	}
}

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
	meta := imageMetadata{Width: 488, Height: 680, Thumbnail: []byte{0xff, 0xd8}}
	msg := buildImageMessage(up, "image/png", "hello", meta)
	if msg.GetURL() != up.URL || msg.GetDirectPath() != up.DirectPath {
		t.Errorf("url/directpath not copied: %+v", msg)
	}
	if msg.GetMimetype() != "image/png" || msg.GetCaption() != "hello" || msg.GetFileLength() != 42 {
		t.Errorf("mime/caption/len wrong: %+v", msg)
	}
	// Dimensions + thumbnail must be embedded — without Width/Height the receiving client guesses
	// the aspect ratio and crops the preview.
	if msg.GetWidth() != 488 || msg.GetHeight() != 680 || len(msg.GetJPEGThumbnail()) == 0 {
		t.Errorf("width/height/thumbnail not embedded: %+v", msg)
	}
	// Empty caption => no caption field set.
	if buildImageMessage(up, "image/png", "", meta).Caption != nil {
		t.Error("empty caption should leave Caption nil")
	}
	// Zero metadata (e.g. undecodable webp) => fields left unset, send still proceeds.
	bare := buildImageMessage(up, "image/webp", "", imageMetadata{})
	if bare.Width != nil || bare.Height != nil || bare.JPEGThumbnail != nil {
		t.Errorf("zero meta should leave width/height/thumbnail nil: %+v", bare)
	}
}

func TestImageMeta(t *testing.T) {
	// A real 488x680 PNG (WhatsApp card aspect), rendered in-process.
	var buf bytes.Buffer
	img := image.NewRGBA(image.Rect(0, 0, 488, 680))
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}

	meta := imageMeta(buf.Bytes())
	if meta.Width != 488 || meta.Height != 680 {
		t.Errorf("dimensions = %dx%d, want 488x680", meta.Width, meta.Height)
	}
	if len(meta.Thumbnail) == 0 {
		t.Error("expected a JPEG thumbnail")
	}
	// Thumbnail long edge scaled to ~thumbnailLongEdge: decode and check.
	cfg, _, err := image.DecodeConfig(bytes.NewReader(meta.Thumbnail))
	if err != nil {
		t.Fatalf("thumbnail not decodable: %v", err)
	}
	if cfg.Height > thumbnailLongEdge || cfg.Width > thumbnailLongEdge {
		t.Errorf("thumbnail %dx%d exceeds long edge %d", cfg.Width, cfg.Height, thumbnailLongEdge)
	}

	// Garbage bytes => zero metadata, no panic.
	if got := imageMeta([]byte("not an image")); got.Width != 0 || got.Thumbnail != nil {
		t.Errorf("garbage input should yield zero meta, got %+v", got)
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
