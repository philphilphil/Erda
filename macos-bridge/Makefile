APP := .build/ErdaBridge.app

.PHONY: build bundle run install clean test lint

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

clean:
	rm -rf .build
