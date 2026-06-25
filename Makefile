# Erda — local dev + deploy helpers.
#
#   make dev      backend + control-panel SPA (no bridge) — the common loop for panel/agent work
#   make dev-all  everything: backend + SPA + WhatsApp bridge; one Ctrl-C kills all
#   make web      run the control-panel SPA dev server on its own
#   make deploy   pull prebuilt GHCR images and restart the Docker stack (on the server)
#
# `concurrently -k` owns the child processes as a single tree and forwards SIGINT/SIGTERM, so one
# Ctrl-C tears them all down without orphans on ports 5167/5173/8088. These targets need node/npx
# (and `make dev-all` also needs go for the bridge).

# Config is env-only: the dev targets source ./.env (same app-native keys as prod — Erda__…,
# WhatsApp__…, AZURE_OPENAI_*, …) so `dotnet watch` boots with them. Copy .env.example to .env first.
# A missing required value stops the backend at startup with a clear error (by design).
BRIDGE_DIR := whatsapp-bridge
WEB_DIR := web

# Export every key in ./.env into the recipe's environment (no-op if .env is absent).
LOAD_ENV := set -a; [ -f .env ] && . ./.env; set +a;

.PHONY: help dev dev-all web deploy

help:
	@echo "make dev      - run backend + SPA only, no bridge (Ctrl-C kills both)"
	@echo "make dev-all  - run everything: backend + SPA + WhatsApp bridge (Ctrl-C kills all)"
	@echo "make web      - run the control-panel SPA dev server (Vite at :5173, proxies /api to :5167)"
	@echo "make deploy   - docker compose pull && docker compose up -d (prebuilt GHCR images)"

# Backend + SPA only (no bridge): the common loop when WhatsApp isn't linked. Open the Vite URL
# (http://localhost:5173) for the panel; the backend serves /api on :5167.
dev:
	@$(LOAD_ENV) npx --yes concurrently -k -n erda,web -c blue,green \
		"dotnet watch --project Erda.Server" \
		"cd $(WEB_DIR) && npm install && npm run dev"

# Everything under one `concurrently -k`: the backend (Development env, config sourced from ./.env,
# hot-reloaded via `dotnet watch`), the Vite SPA (:5173, proxies /api + the SSE stream to :5167), and
# the WhatsApp bridge. The bridge is built and exec'd so it is concurrently's direct child and dies
# cleanly with -k (unlike `go run`, which would orphan the compiled binary on the socket / port 8088).
dev-all:
	@$(LOAD_ENV) npx --yes concurrently -k -n erda,web,wa -c blue,green,magenta \
		"dotnet watch --project Erda.Server" \
		"cd $(WEB_DIR) && npm install && npm run dev" \
		"cd $(BRIDGE_DIR) && go build -o whatsapp-bridge . && exec ./whatsapp-bridge"

# Control-panel SPA dev server (Vite) on its own. Proxies /api (and the SSE stream) to the backend
# on :5167, so run `make dev`/`make dev-all` alongside. `npm install` is a fast no-op if current.
web:
	@cd $(WEB_DIR) && npm install && npm run dev

# Server: pull the prebuilt images and restart the stack (no git pull, no --build). The images are
# built and pushed to GHCR by CI (.github/workflows/build.yml); the server only needs this compose
# file + ./.env. Komodo can run this same command instead (it owns the production deploy).
deploy:
	docker compose pull && docker compose up -d
