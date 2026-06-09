package main

import (
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
)

// sendRequest is the body of POST /send.
type sendRequest struct {
	To   string `json:"to"`   // destination JID, e.g. 4915123456789@s.whatsapp.net
	Text string `json:"text"` // message body
}

// newServer builds the outbound HTTP server (Erda -> WhatsApp).
//
// Routes:
//
//	GET  /healthz     -> 200 "ok"  (no auth)
//	POST /send        -> send a WhatsApp text message (requires X-Bridge-Secret)
//	POST /send-media  -> upload + send an image from the shared media volume (requires X-Bridge-Secret)
func newServer(cfg Config, client *whatsmeow.Client) *http.Server {
	mux := http.NewServeMux()

	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})

	mux.HandleFunc("/send", sendHandler(cfg, client))
	mux.HandleFunc("/send-media", sendMediaHandler(cfg, client))

	return &http.Server{
		Addr:              cfg.Listen,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
	}
}

// sendHandler returns the handler for POST /send.
func sendHandler(cfg Config, client *whatsmeow.Client) http.HandlerFunc {
	return func(w http.ResponseWriter, req *http.Request) {
		if req.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}

		// Authenticate via the shared secret. Constant-time compare avoids
		// leaking the secret length/content through timing.
		if !secretEqual(req.Header.Get("X-Bridge-Secret"), cfg.SharedSecret) {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}

		var body sendRequest
		dec := json.NewDecoder(http.MaxBytesReader(w, req.Body, 1<<20)) // 1 MiB cap
		if err := dec.Decode(&body); err != nil {
			http.Error(w, "invalid JSON body", http.StatusBadRequest)
			return
		}
		if strings.TrimSpace(body.To) == "" || body.Text == "" {
			http.Error(w, "fields 'to' and 'text' are required", http.StatusBadRequest)
			return
		}

		to, err := types.ParseJID(body.To)
		if err != nil {
			http.Error(w, "invalid 'to' JID: "+err.Error(), http.StatusBadRequest)
			return
		}

		msg := &waE2E.Message{Conversation: proto.String(body.Text)}
		resp, err := client.SendMessage(req.Context(), to, msg)
		if err != nil {
			slog.Warn("send failed", "to", to.String(), "error", err)
			http.Error(w, "send failed: "+err.Error(), http.StatusInternalServerError)
			return
		}

		slog.Info("outbound message sent", "to", to.String(), "id", resp.ID)
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	}
}

// secretEqual compares two secrets in constant time.
func secretEqual(got, want string) bool {
	return subtle.ConstantTimeCompare([]byte(got), []byte(want)) == 1
}

// sendMediaRequest is the body of POST /send-media. mediaPath must point at a file inside MEDIA_DIR
// (the volume shared with Erda); caption is optional.
type sendMediaRequest struct {
	To        string `json:"to"`        // destination JID
	MediaPath string `json:"mediaPath"` // absolute path inside MEDIA_DIR
	Caption   string `json:"caption"`   // optional caption
}

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
		URL:           proto.String(up.URL),
		DirectPath:    proto.String(up.DirectPath),
		MediaKey:      up.MediaKey,
		FileEncSHA256: up.FileEncSHA256,
		FileSHA256:    up.FileSHA256,
		FileLength:    proto.Uint64(up.FileLength),
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
