// Command whatsapp-bridge is a small, dumb relay between WhatsApp and the Erda
// personal agent. It owns the WhatsApp multi-device socket/session (via the
// unofficial whatsmeow library) and bridges messages over local HTTP.
//
// It contains NO API keys and NO model logic:
//   - Inbound  (WhatsApp -> Erda): each accepted message is POSTed as JSON to
//     ERDA_INBOUND_URL.
//   - Outbound (Erda -> WhatsApp): an HTTP server on BRIDGE_LISTEN exposes
//     POST /send to push a text message back to a WhatsApp JID.
//
// On first run with no saved session it prints a QR code to the terminal; on
// subsequent runs it connects silently using the session stored in SESSION_DB.
package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/joho/godotenv"
	"github.com/mdp/qrterminal/v3"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store/sqlstore"
	"go.mau.fi/whatsmeow/types"
	waLog "go.mau.fi/whatsmeow/util/log"

	// Pure-Go SQLite driver. It registers itself under the name "sqlite", which
	// is the driver name we hand to sqlstore.New below. Using modernc keeps the
	// whole binary CGO-free so `go build ./...` works without a C toolchain.
	_ "modernc.org/sqlite"
)

// Config holds all runtime configuration, sourced strictly from the
// environment (a local .env file is loaded first if present).
type Config struct {
	InboundURL   string // ERDA_INBOUND_URL: where inbound messages are POSTed.
	Listen       string // BRIDGE_LISTEN: localhost host:port for the /send server.
	SharedSecret string // SHARED_SECRET: value of the X-Bridge-Secret header.
	OwnerJID     string // OWNER_JID: only relay messages from this user.
	SessionDB    string // SESSION_DB: path to the whatsmeow SQLite session store.
	MediaDir     string // MEDIA_DIR: directory for downloaded media.
}

// ownerUser returns the bare user part of OWNER_JID (the digits before "@"),
// so comparisons ignore any device/agent suffixes.
func (c Config) ownerUser() string {
	user := c.OwnerJID
	if i := strings.IndexByte(user, '@'); i >= 0 {
		user = user[:i]
	}
	// Strip any device suffix like ":3" just in case.
	if i := strings.IndexByte(user, ':'); i >= 0 {
		user = user[:i]
	}
	return user
}

// loadConfig reads configuration from the environment, applying defaults and
// validating that the required variables are present.
func loadConfig() (Config, error) {
	// Best-effort load of a local .env; missing file is not an error.
	if err := godotenv.Load(); err != nil && !os.IsNotExist(err) {
		slog.Warn("could not load .env file", "error", err)
	}

	cfg := Config{
		InboundURL:   os.Getenv("ERDA_INBOUND_URL"),
		Listen:       getenvDefault("BRIDGE_LISTEN", "127.0.0.1:8088"),
		SharedSecret: os.Getenv("SHARED_SECRET"),
		OwnerJID:     os.Getenv("OWNER_JID"),
		SessionDB:    getenvDefault("SESSION_DB", "./whatsmeow-session.db"),
		MediaDir:     getenvDefault("MEDIA_DIR", "/tmp/erda-bridge"),
	}

	var missing []string
	if cfg.InboundURL == "" {
		missing = append(missing, "ERDA_INBOUND_URL")
	}
	if cfg.SharedSecret == "" {
		missing = append(missing, "SHARED_SECRET")
	}
	if cfg.OwnerJID == "" {
		missing = append(missing, "OWNER_JID")
	}
	if len(missing) > 0 {
		return Config{}, fmt.Errorf("missing required environment variables: %s", strings.Join(missing, ", "))
	}
	return cfg, nil
}

func getenvDefault(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func main() {
	slog.SetDefault(slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelInfo})))

	cfg, err := loadConfig()
	if err != nil {
		slog.Error("configuration error", "error", err)
		os.Exit(1)
	}

	// Ensure the media directory exists.
	if err := os.MkdirAll(cfg.MediaDir, 0o755); err != nil {
		slog.Error("could not create media dir", "dir", cfg.MediaDir, "error", err)
		os.Exit(1)
	}

	// Top-level context cancelled on SIGINT/SIGTERM for graceful shutdown.
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	client, err := newWhatsAppClient(ctx, cfg)
	if err != nil {
		slog.Error("failed to set up WhatsApp client", "error", err)
		os.Exit(1)
	}

	// Wire up the inbound relay (WhatsApp -> Erda).
	relay := newRelay(cfg, client)
	client.AddEventHandler(relay.handleEvent)

	// Connect to WhatsApp, handling the QR login flow on first run.
	if err := connect(ctx, client); err != nil {
		slog.Error("failed to connect to WhatsApp", "error", err)
		os.Exit(1)
	}

	// Announce availability once so the server has our pushname; chat-presence (typing) indicators
	// can't be sent otherwise. A brand-new session may not have a pushname yet — that's fine, it's
	// set on a later connect, so we ignore ErrNoPushName instead of failing.
	if err := client.SendPresence(ctx, types.PresenceAvailable); err != nil && !errors.Is(err, whatsmeow.ErrNoPushName) {
		slog.Warn("could not send initial presence", "error", err)
	}

	// Start the outbound HTTP server (Erda -> WhatsApp).
	srv := newServer(cfg, client)
	go func() {
		slog.Info("HTTP server listening", "addr", cfg.Listen)
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			slog.Error("HTTP server error", "error", err)
			stop() // trigger shutdown if the server dies unexpectedly
		}
	}()

	// Block until a shutdown signal arrives.
	<-ctx.Done()
	slog.Info("shutdown signal received, stopping")

	// Graceful HTTP shutdown with a short timeout, then disconnect WhatsApp.
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := srv.Shutdown(shutdownCtx); err != nil {
		slog.Warn("HTTP server shutdown error", "error", err)
	}
	client.Disconnect()
	slog.Info("bye")
}

// newWhatsAppClient opens (or creates) the SQLite session store and constructs
// a whatsmeow client bound to the first device in the store.
func newWhatsAppClient(ctx context.Context, cfg Config) (*whatsmeow.Client, error) {
	dbLog := waLog.Stdout("Database", "INFO", true)

	// modernc.org/sqlite registers itself as driver name "sqlite". The DSN is a
	// plain file path; _pragma enables foreign keys, matching whatsmeow's
	// expectations from its mattn-based examples.
	dsn := fmt.Sprintf("file:%s?_pragma=foreign_keys(1)&_pragma=busy_timeout(5000)", cfg.SessionDB)
	container, err := sqlstore.New(ctx, "sqlite", dsn, dbLog)
	if err != nil {
		return nil, fmt.Errorf("open session store: %w", err)
	}

	// GetFirstDevice returns a fresh device if the store is empty (first run).
	deviceStore, err := container.GetFirstDevice(ctx)
	if err != nil {
		return nil, fmt.Errorf("get device: %w", err)
	}

	clientLog := waLog.Stdout("Client", "INFO", true)
	client := whatsmeow.NewClient(deviceStore, clientLog)
	return client, nil
}

// connect connects the client to WhatsApp. If there is no stored session
// (client.Store.ID == nil), it renders QR codes to the terminal until login
// completes; otherwise it connects silently with the saved session.
func connect(ctx context.Context, client *whatsmeow.Client) error {
	if client.Store.ID == nil {
		// First run: no session yet, so we need to link via QR.
		qrChan, err := client.GetQRChannel(ctx)
		if err != nil {
			return fmt.Errorf("get QR channel: %w", err)
		}
		if err := client.Connect(); err != nil {
			return fmt.Errorf("connect: %w", err)
		}
		for evt := range qrChan {
			switch evt.Event {
			case "code":
				slog.Info("scan this with WhatsApp > Linked devices > Link a device")
				// Render the QR to the terminal. Half-block keeps it compact.
				qrterminal.GenerateHalfBlock(evt.Code, qrterminal.L, os.Stdout)
			case "success":
				slog.Info("logged in successfully")
			case "timeout":
				slog.Warn("QR code login timed out")
			default:
				slog.Info("login event", "event", evt.Event)
			}
		}
		return nil
	}

	// Returning user: connect silently with the saved session.
	if err := client.Connect(); err != nil {
		return fmt.Errorf("connect: %w", err)
	}
	slog.Info("connected with existing session", "jid", jidString(client.Store.ID))
	return nil
}

// jidString safely renders a possibly-nil JID pointer for logging.
func jidString(j *types.JID) string {
	if j == nil {
		return "<none>"
	}
	return j.String()
}
