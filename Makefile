# Erda — local dev + deploy helpers.
#
#   make dev      backend + control-panel SPA (no bridge) — the common loop for panel/agent work
#   make dev-all  everything: backend + SPA + WhatsApp bridge; one Ctrl-C kills all
#   make web      run the control-panel SPA dev server on its own
#   make deploy   pull latest and (re)build + restart the Docker stack (on the server)
#
# `concurrently -k` owns the child processes as a single tree and forwards SIGINT/SIGTERM, so one
# Ctrl-C tears them all down without orphans on ports 5167/5173/8088. These targets need node/npx
# (and `make dev-all` also needs go for the bridge).

BRIDGE_DIR := whatsapp-bridge
WEB_DIR := web

.PHONY: help dev dev-all web deploy

help:
	@echo "make dev      - run backend + SPA only, no bridge (Ctrl-C kills both)"
	@echo "make dev-all  - run everything: backend + SPA + WhatsApp bridge (Ctrl-C kills all)"
	@echo "make web      - run the control-panel SPA dev server (Vite at :5173, proxies /api to :5167)"
	@echo "make deploy   - git pull && docker compose up -d --build"

# Backend + SPA only (no bridge): the common loop when WhatsApp isn't linked. Open the Vite URL
# (http://localhost:5173) for the panel; the backend serves /api on :5167.
dev:
	@npx --yes concurrently -k -n erda,web -c blue,green \
		"dotnet watch --project Erda.Server" \
		"cd $(WEB_DIR) && npm install && npm run dev"

# Everything under one `concurrently -k`: the backend (Development env -> appsettings.Development.json
# on :5167, hot-reloaded via `dotnet watch`), the Vite SPA (:5173, proxies /api + the SSE
# stream to :5167), and the WhatsApp bridge. The bridge is built and exec'd so it is concurrently's
# direct child and dies cleanly with -k (unlike `go run`, which would orphan the compiled binary on
# the WhatsApp socket / port 8088).
dev-all:
	@npx --yes concurrently -k -n erda,web,wa -c blue,green,magenta \
		"dotnet watch --project Erda.Server" \
		"cd $(WEB_DIR) && npm install && npm run dev" \
		"cd $(BRIDGE_DIR) && go build -o whatsapp-bridge . && exec ./whatsapp-bridge"

# Control-panel SPA dev server (Vite) on its own. Proxies /api (and the SSE stream) to the backend
# on :5167, so run `make dev`/`make dev-all` alongside. `npm install` is a fast no-op if current.
web:
	@cd $(WEB_DIR) && npm install && npm run dev

# Server: pull the latest commit and rebuild/restart the stack. Reads ./.env.
deploy:
	git pull && docker compose up -d --build
