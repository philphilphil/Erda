#!/usr/bin/env bash
# Entrypoint for the Obsidian Sync sidecar. Modes:
#
#   sync   (default) — link the vault if needed, then sync continuously (the long-running service)
#   setup            — one-time interactive bootstrap: `ob login` + `ob sync-setup` (answers the
#                      E2E-encryption password prompt here, where a TTY exists, so the key persists
#                      in /config). Run via: docker compose run --rm obsidian-sync setup
#   login            — just re-authenticate (e.g. after a token revoke)
#
# Auth: if OBSIDIAN_AUTH_TOKEN is set (from .env), the client uses it automatically and `login` is
# unnecessary. Otherwise the session persisted by `setup`/`login` in the /config volume is reused.
set -euo pipefail

mode="${1:-sync}"

case "$mode" in
  login)
    exec ob login
    ;;

  setup)
    : "${OBSIDIAN_VAULT_NAME:?set OBSIDIAN_VAULT_NAME in .env}"
    # Skip the interactive login when a token is already injected via the environment.
    if [ -z "${OBSIDIAN_AUTH_TOKEN:-}" ]; then
      ob login
    fi
    ob sync-setup --vault "$OBSIDIAN_VAULT_NAME"
    # One-time initial pull so the vault volume is already populated before `up` starts erda.
    exec ob sync
    ;;

  sync)
    : "${OBSIDIAN_VAULT_NAME:?set OBSIDIAN_VAULT_NAME in .env}"
    # Idempotent: links /vault to the remote vault on first run; a no-op (tolerated) once linked.
    ob sync-setup --vault "$OBSIDIAN_VAULT_NAME" || true
    exec ob sync --continuous
    ;;

  *)
    # Escape hatch: run any ob subcommand directly, e.g. `... obsidian-sync sync-list-remote`.
    exec ob "$@"
    ;;
esac
