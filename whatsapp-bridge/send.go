package main

import (
	"crypto/subtle"
	"encoding/json"
	"log/slog"
	"net/http"
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
//	GET  /healthz  -> 200 "ok"  (no auth)
//	POST /send     -> send a WhatsApp text message (requires X-Bridge-Secret)
func newServer(cfg Config, client *whatsmeow.Client) *http.Server {
	mux := http.NewServeMux()

	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})

	mux.HandleFunc("/send", sendHandler(cfg, client))

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
