# Erda — local dev + deploy helpers.
#
#   make dev      run Erda locally (dotnet run; DevUI at http://localhost:5167/devui)
#   make dev-wa   run Erda + the WhatsApp bridge together; one Ctrl-C kills both
#   make deploy   pull latest and (re)build + restart the Docker stack (on the server)
#
# `concurrently -k` owns both processes as a single tree and forwards SIGINT/SIGTERM, so one
# Ctrl-C tears both down without orphans on ports 5167/8088. `dev-wa` needs node/npx.

BRIDGE_DIR := whatsapp-bridge

.PHONY: help dev dev-wa deploy

help:
	@echo "make dev     - run Erda locally (dotnet run)"
	@echo "make dev-wa  - run Erda + the WhatsApp bridge together (Ctrl-C kills both)"
	@echo "make deploy  - git pull && docker compose up -d --build"

# Erda only. Development env, so it reads appsettings.Development.json and mounts DevUI.
dev:
	@dotnet run

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
