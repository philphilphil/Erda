package main

import (
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
