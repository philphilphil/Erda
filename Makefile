# Erda — local dev + deploy helpers.
#
#   make dev      run Erda locally (dotnet run; DevUI at http://localhost:5167/devui)
#   make dev-wa   run Erda + the WhatsApp bridge together; one Ctrl-C kills both
#   make deploy   pull latest and (re)build + restart the Docker stack (on the server)
#
# `concurrently -k` owns both processes as a single tree and forwards SIGINT/SIGTERM, so one
# Ctrl-C tears both down without orphans on ports 5167/8088. `dev-wa` needs node/npx.

BRIDGE_DIR := whatsapp-bridge
WEB_DIR := web

.PHONY: help dev dev-wa dev-web web deploy

help:
	@echo "make dev      - run Erda backend locally (dotnet run; DevUI at :5167/devui)"
	@echo "make web      - run the control-panel SPA dev server (Vite at :5173, proxies /api to :5167)"
	@echo "make dev-web  - run backend + SPA dev server together (Ctrl-C kills both)"
	@echo "make dev-wa   - run Erda + the WhatsApp bridge together (Ctrl-C kills both)"
	@echo "make deploy   - git pull && docker compose up -d --build"

# Erda only. Development env, so it reads appsettings.Development.json and mounts DevUI.
dev:
	@dotnet run

# Control-panel SPA dev server (Vite). Proxies /api (and the SSE stream) to the backend on :5167,
# so run `make dev` alongside it (or use `make dev-web`). `npm install` is a fast no-op if current.
web:
	@cd $(WEB_DIR) && npm install && npm run dev

# Backend + SPA dev server under `concurrently -k`: one Ctrl-C tears both down. Open the Vite URL
# (http://localhost:5173) for the panel; the backend serves /api and /devui on :5167.
dev-web:
	@npx --yes concurrently -k -n erda,web -c blue,green \
		"dotnet run" \
		"cd $(WEB_DIR) && npm install && npm run dev"

# Erda + bridge under `concurrently -k`. The bridge is built and exec'd so it is
# concurrently's direct child and dies cleanly with -k (unlike `go run`, which would
# orphan the compiled binary on the WhatsApp socket / port 8088).
dev-wa:
	@npx --yes concurrently -k -n erda,wa -c blue,magenta \
		"dotnet run" \
		"cd $(BRIDGE_DIR) && go build -o whatsapp-bridge . && exec ./whatsapp-bridge"

# Server: pull the latest commit and rebuild/restart the stack. Reads ./.env.
deploy:
	git pull && docker compose up -d --build
