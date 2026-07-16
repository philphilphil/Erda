package main

import (
	"bytes"
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"image"
	"image/jpeg"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	// Image decoders for imageMeta (dimensions + thumbnail). No stdlib webp decoder — webp sends
	// simply go out without dimensions, which only affects the preview aspect.
	_ "image/gif"
	_ "image/png"

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

// presenceRequest is the body of POST /presence.
type presenceRequest struct {
	To    string `json:"to"`    // destination JID
	State string `json:"state"` // "composing" (typing…) or "paused" (cleared)
}

// newServer builds the outbound HTTP server (Erda -> WhatsApp).
//
// Routes:
//
//	GET  /healthz     -> 200 "ok"  (no auth)
//	POST /send        -> send a WhatsApp text message (requires X-Bridge-Secret)
//	POST /send-media  -> upload + send an image from the shared media volume (requires X-Bridge-Secret)
//	POST /presence    -> set the chat typing indicator (requires X-Bridge-Secret)
func newServer(cfg Config, client *whatsmeow.Client) *http.Server {
	mux := http.NewServeMux()

	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})

	mux.HandleFunc("/send", sendHandler(cfg, client))
	mux.HandleFunc("/send-media", sendMediaHandler(cfg, client))
	mux.HandleFunc("/presence", presenceHandler(cfg, client))

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

// presenceHandler returns the handler for POST /presence: it sets the chat presence (typing
// indicator) for a JID. Erda drives this around an agent turn so the owner sees Erda "typing…"
// while it generates. Mirror of sendHandler's auth + decode flow.
func presenceHandler(cfg Config, client *whatsmeow.Client) http.HandlerFunc {
	return func(w http.ResponseWriter, req *http.Request) {
		if req.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}

		if !secretEqual(req.Header.Get("X-Bridge-Secret"), cfg.SharedSecret) {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}

		var body presenceRequest
		dec := json.NewDecoder(http.MaxBytesReader(w, req.Body, 1<<20)) // 1 MiB cap
		if err := dec.Decode(&body); err != nil {
			http.Error(w, "invalid JSON body", http.StatusBadRequest)
			return
		}
		if strings.TrimSpace(body.To) == "" {
			http.Error(w, "field 'to' is required", http.StatusBadRequest)
			return
		}

		to, err := types.ParseJID(body.To)
		if err != nil {
			http.Error(w, "invalid 'to' JID: "+err.Error(), http.StatusBadRequest)
			return
		}

		var state types.ChatPresence
		switch body.State {
		case "composing":
			state = types.ChatPresenceComposing
		case "paused":
			state = types.ChatPresencePaused
		default:
			http.Error(w, "field 'state' must be 'composing' or 'paused'", http.StatusBadRequest)
			return
		}

		if err := client.SendChatPresence(req.Context(), to, state, types.ChatPresenceMediaText); err != nil {
			slog.Warn("send presence failed", "to", to.String(), "error", err)
			http.Error(w, "send presence failed: "+err.Error(), http.StatusInternalServerError)
			return
		}

		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	}
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

		msg := &waE2E.Message{ImageMessage: buildImageMessage(uploaded, mime, body.Caption, imageMeta(data))}
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
// meta (dimensions + preview thumbnail) matters for rendering: without Width/Height the receiving
// client guesses the bubble's aspect ratio and CROPS the image (a forwarded copy re-adds the metadata,
// which is why forwards used to display correctly while our sends did not).
func buildImageMessage(up whatsmeow.UploadResponse, mime, caption string, meta imageMetadata) *waE2E.ImageMessage {
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
	if meta.Width > 0 && meta.Height > 0 {
		msg.Width = proto.Uint32(uint32(meta.Width))
		msg.Height = proto.Uint32(uint32(meta.Height))
	}
	if len(meta.Thumbnail) > 0 {
		msg.JPEGThumbnail = meta.Thumbnail
	}
	return msg
}

// imageMetadata is what buildImageMessage embeds for correct client-side rendering.
type imageMetadata struct {
	Width, Height int
	Thumbnail     []byte // small JPEG preview, or nil
}

// thumbnailLongEdge is the target long edge of the embedded preview thumbnail (WhatsApp clients
// typically embed ~100 px).
const thumbnailLongEdge = 100

// imageMeta decodes the image's dimensions and renders a small JPEG preview thumbnail. Best-effort:
// an undecodable image (e.g. webp, which the stdlib cannot decode) yields zero metadata and the send
// proceeds without it.
func imageMeta(data []byte) imageMetadata {
	cfg, _, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil || cfg.Width <= 0 || cfg.Height <= 0 {
		return imageMetadata{}
	}
	meta := imageMetadata{Width: cfg.Width, Height: cfg.Height}

	src, _, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		return meta // dimensions still help; just no thumbnail
	}

	// Scale so the long edge is at most thumbnailLongEdge (never upscale), nearest-neighbor — stdlib
	// only, plenty for a blurred preview placeholder. Ceiling division: rounding down would leave the
	// long edge above the target (e.g. 680/6 = 113 > 100).
	scale := (max(cfg.Width, cfg.Height) + thumbnailLongEdge - 1) / thumbnailLongEdge
	if scale < 1 {
		scale = 1
	}
	w, h := max(cfg.Width/scale, 1), max(cfg.Height/scale, 1)
	thumb := image.NewRGBA(image.Rect(0, 0, w, h))
	bounds := src.Bounds()
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			thumb.Set(x, y, src.At(bounds.Min.X+x*scale, bounds.Min.Y+y*scale))
		}
	}

	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, thumb, &jpeg.Options{Quality: 70}); err != nil {
		return meta
	}
	meta.Thumbnail = buf.Bytes()
	return meta
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
