package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
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
	"go.mau.fi/whatsmeow/types/events"
)

// inboundPayload is the exact JSON shape POSTed to ERDA_INBOUND_URL.
//
// type is one of "text" | "audio" | "image".
//   - text:  Text set to the body; MediaPath/MimeType omitted.
//   - image: Text set to the caption (may be ""); MediaPath/MimeType filled.
//   - audio: Text empty; MediaPath/MimeType filled.
type inboundPayload struct {
	From      string `json:"from"`                // bare sender JID (the owner)
	Chat      string `json:"chat"`                // JID to reply to (the owner)
	Type      string `json:"type"`                // "text" | "audio" | "image"
	Text      string `json:"text"`                // body or caption
	MediaPath string `json:"mediaPath,omitempty"` // absolute path to downloaded media
	MimeType  string `json:"mimeType,omitempty"`  // media mimetype
	MessageID string `json:"messageId"`           // WhatsApp message ID
	Timestamp int64  `json:"timestamp"`           // unix seconds
}

// relay handles inbound WhatsApp messages and forwards accepted ones to Erda.
type relay struct {
	cfg    Config
	client *whatsmeow.Client
	http   *http.Client
}

func newRelay(cfg Config, client *whatsmeow.Client) *relay {
	return &relay{
		cfg:    cfg,
		client: client,
		http:   &http.Client{Timeout: 15 * time.Second},
	}
}

// handleEvent is the whatsmeow event handler. We only care about *events.Message.
func (r *relay) handleEvent(evt any) {
	if msg, ok := evt.(*events.Message); ok {
		r.handleMessage(msg)
	}
}

// handleMessage applies the filtering/typing rules and forwards to Erda.
func (r *relay) handleMessage(evt *events.Message) {
	info := evt.Info

	// 1. Drop group/broadcast messages.
	if info.IsGroup {
		slog.Debug("ignoring group message", "chat", info.Chat.String())
		return
	}

	// 1b. Drop anything not from the owner. Compare on the bare user part so
	// device/agent suffixes (and AD-JIDs) don't cause false negatives.
	sender := info.Sender.ToNonAD()
	if sender.User != r.cfg.ownerUser() {
		slog.Debug("ignoring non-owner message", "sender", info.Sender.String())
		return
	}

	// Reply target: the owner's bare chat JID.
	chat := info.Chat.ToNonAD()

	payload := inboundPayload{
		From:      sender.String(),
		Chat:      chat.String(),
		MessageID: info.ID,
		Timestamp: info.Timestamp.Unix(),
	}

	// 2. Determine the message type and (for media) download + persist it.
	switch {
	case isTextMessage(evt.Message):
		payload.Type = "text"
		payload.Text = textBody(evt.Message)

	case evt.Message.GetImageMessage() != nil:
		img := evt.Message.GetImageMessage()
		path, err := r.downloadMedia(img, img.GetMimetype())
		if err != nil {
			slog.Warn("image download failed", "id", info.ID, "error", err)
			return
		}
		payload.Type = "image"
		payload.Text = img.GetCaption() // "" if no caption
		payload.MediaPath = path
		payload.MimeType = img.GetMimetype()

	case evt.Message.GetAudioMessage() != nil:
		// audioMessage covers both regular audio and PTT voice notes.
		aud := evt.Message.GetAudioMessage()
		path, err := r.downloadMedia(aud, aud.GetMimetype())
		if err != nil {
			slog.Warn("audio download failed", "id", info.ID, "error", err)
			return
		}
		payload.Type = "audio"
		payload.MediaPath = path
		payload.MimeType = aud.GetMimetype()

	default:
		// Sticker / video / document / etc. — log and skip for now.
		slog.Info("skipping unsupported message type", "id", info.ID, "type", info.Type, "mediaType", info.MediaType)
		return
	}

	slog.Info("inbound message", "type", payload.Type, "from", payload.From, "id", payload.MessageID)
	r.forward(payload)
}

// isTextMessage reports whether the message is a plain or extended text message.
func isTextMessage(m *waE2E.Message) bool {
	if m.GetConversation() != "" {
		return true
	}
	// ExtendedTextMessage is used for links, replies, formatted text, etc.
	return m.GetExtendedTextMessage() != nil
}

// textBody extracts the text body from a plain or extended text message.
func textBody(m *waE2E.Message) string {
	if c := m.GetConversation(); c != "" {
		return c
	}
	return m.GetExtendedTextMessage().GetText()
}

// downloadMedia downloads a media sub-message, writes it to MEDIA_DIR with a
// random filename and an extension derived from its mimetype, and returns the
// absolute file path.
//
// The argument is a whatsmeow.DownloadableMessage — the concrete media
// sub-message (e.g. *waE2E.ImageMessage / *waE2E.AudioMessage), NOT the whole
// envelope.
func (r *relay) downloadMedia(dl whatsmeow.DownloadableMessage, mimeType string) (string, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()

	data, err := r.client.Download(ctx, dl)
	if err != nil {
		return "", fmt.Errorf("download: %w", err)
	}

	name, err := randomName()
	if err != nil {
		return "", err
	}
	path := filepath.Join(r.cfg.MediaDir, name+extensionForMime(mimeType))

	if err := os.WriteFile(path, data, 0o644); err != nil {
		return "", fmt.Errorf("write media: %w", err)
	}
	slog.Info("saved media", "path", path, "bytes", len(data), "mime", mimeType)
	return path, nil
}

// forward POSTs the payload to ERDA_INBOUND_URL with the shared-secret header.
// Erda returns 202 and processes asynchronously; a non-2xx is logged as a
// warning but is not fatal (the reply later arrives via /send).
func (r *relay) forward(payload inboundPayload) {
	body, err := json.Marshal(payload)
	if err != nil {
		slog.Error("marshal inbound payload", "error", err)
		return
	}

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, r.cfg.InboundURL, bytes.NewReader(body))
	if err != nil {
		slog.Error("build inbound request", "error", err)
		return
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-Bridge-Secret", r.cfg.SharedSecret)

	resp, err := r.http.Do(req)
	if err != nil {
		slog.Warn("forward to Erda failed", "error", err)
		return
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		slog.Warn("Erda returned non-2xx", "status", resp.StatusCode, "id", payload.MessageID)
		return
	}
	slog.Debug("forwarded to Erda", "status", resp.StatusCode, "id", payload.MessageID)
}

// randomName returns 8 random hex characters for use as a media filename stem.
func randomName() (string, error) {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("random name: %w", err)
	}
	return hex.EncodeToString(b), nil
}

// extensionForMime maps a mimetype to a file extension. WhatsApp mimetypes
// often carry parameters (e.g. "audio/ogg; codecs=opus"), so we strip those
// first. Falls back to a sensible default per media family, then ".bin".
func extensionForMime(mime string) string {
	// Drop any "; codecs=..." parameters and normalise.
	base := strings.ToLower(strings.TrimSpace(strings.SplitN(mime, ";", 2)[0]))

	switch base {
	case "audio/ogg":
		return ".ogg"
	case "audio/mpeg", "audio/mp3":
		return ".mp3"
	case "audio/mp4", "audio/m4a", "audio/aac":
		return ".m4a"
	case "audio/amr":
		return ".amr"
	case "audio/wav", "audio/x-wav":
		return ".wav"
	case "image/jpeg":
		return ".jpg"
	case "image/png":
		return ".png"
	case "image/webp":
		return ".webp"
	case "image/gif":
		return ".gif"
	}

	// Family-level fallbacks for anything we didn't enumerate.
	switch {
	case strings.HasPrefix(base, "audio/"):
		return ".ogg"
	case strings.HasPrefix(base, "image/"):
		return ".jpg"
	default:
		return ".bin"
	}
}
