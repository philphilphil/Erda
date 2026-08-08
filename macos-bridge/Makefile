APP := .build/ErdaBridge.app

.PHONY: build bundle run install stop deploy clean test lint

build:
	swift build -c release --arch arm64

test: lint
	swift test

lint:
	./scripts/lint-forbidden.sh

bundle:
	./scripts/bundle.sh

# NEVER `swift run`: a bare SwiftPM binary has no bundle id, so TCC attributes the
# Reminders request to the terminal emulator instead of ErdaBridge.
run: bundle
	open "$(CURDIR)/$(APP)"

install: bundle
	rm -rf /Applications/ErdaBridge.app
	cp -R "$(APP)" /Applications/ErdaBridge.app

# Quit the running menu-bar app so its bundle can be replaced. SIGTERM, not an Apple event:
# `osascript -e 'quit app "ErdaBridge"'` would make the terminal ask for Automation access to
# ErdaBridge — a TCC prompt you'd have to answer on every fresh shell. A no-op when nothing is
# running (pkill exits 1). Falls back to SIGKILL only if it is still up after ~5s.
stop:
	@pkill -x ErdaBridge 2>/dev/null || true
	@for i in $$(seq 1 20); do \
		pgrep -x ErdaBridge >/dev/null 2>&1 || break; \
		sleep 0.25; \
	done
	@if pgrep -x ErdaBridge >/dev/null 2>&1; then \
		echo "==> ErdaBridge ignored SIGTERM, forcing"; pkill -9 -x ErdaBridge || true; sleep 0.5; \
	fi
	@echo "==> ErdaBridge stopped"

# Build, then swap the installed app and start it again. Order matters: bundle FIRST, so a broken
# build leaves the running bridge alone instead of stopping it and finding nothing to install.
# The copy re-uses install's steps rather than depending on it, so bundle doesn't run twice.
#
# Reinstalling in place keeps the Reminders/Calendar grants and the Login Item: the bundle id and
# the team-ID-pinned designated requirement (scripts/bundle.sh) are what TCC matches, and neither
# changes across builds. A firewall prompt can reappear — approve it once at the new binary.
deploy: bundle
	@$(MAKE) stop
	rm -rf /Applications/ErdaBridge.app
	cp -R "$(APP)" /Applications/ErdaBridge.app
	open /Applications/ErdaBridge.app
	@echo "==> deployed and launched: /Applications/ErdaBridge.app"

clean:
	rm -rf .build
