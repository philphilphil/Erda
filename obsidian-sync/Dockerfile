# Obsidian Sync headless client — keeps the shared `vault` volume continuously synced from inside
# the stack, replacing the former host-side requirement (Syncthing / obsidian-git). This is the
# OFFICIAL client: https://github.com/obsidianmd/obsidian-headless (npm `obsidian-headless`, Node 22+,
# Obsidian Sync 1.12+). Same end-to-end encryption as the desktop app; runs fully headless.
#
# Auth is injected much like codex: either OBSIDIAN_AUTH_TOKEN from .env (non-interactive), or a
# one-time `docker compose run --rm obsidian-sync setup` that logs in + links the vault and persists
# the session into the /config volume (mirrors the WhatsApp-bridge QR bootstrap).
FROM node:22-slim

# Pin the client. It is an OPEN BETA — breaking changes are possible, so bump deliberately to upgrade.
# >= 0.0.3 is REQUIRED for headless E2E-encrypted vaults: earlier versions store the vault key via the
# OS keyring (libsecret/D-Bus), which isn't present in a container and fails sync-setup.
ARG OBSIDIAN_HEADLESS_VERSION=0.0.12
RUN npm install -g "obsidian-headless@${OBSIDIAN_HEADLESS_VERSION}" \
 && ob --version

# Force HOME into the mounted /config volume so the persisted auth token + sync-setup state survive
# restarts regardless of which path the client picks (~/.config/obsidian-headless or ~/.obsidian-headless).
# Runs as uid 1000 (compose `user:`) to match erda's owner on the shared vault volume.
ENV HOME=/config

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

WORKDIR /vault
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["sync"]
