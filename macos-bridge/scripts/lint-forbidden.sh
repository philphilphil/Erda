#!/bin/bash
# Fail if Sources/ references any API this project must never use.
#
# The module boundaries (BridgeHTTP never links EventKit, only BridgeEventKit does) are the
# security architecture, enforced by the compiler. This lint backstops the *content* rule
# alongside it: no shell-out, no scripting bridge, no WebKit, no Shortcuts/Intents path.
# Grepping is enough — this is a shell script the Makefile runs, not code the app ships, so it
# never needs to touch a forbidden API itself.
set -euo pipefail

cd "$(dirname "$0")/.."

# Word-boundary patterns for bare identifiers, so this doesn't flag substrings like
# `NSTaskbarThing` or a doc comment mentioning `NSWorkspace` in prose (there are none today, but
# the boundary keeps false positives out as the codebase grows).
PATTERNS=(
  '\bProcess\b'
  '\bNSTask\b'
  '\bNSAppleScript\b'
  '\bOSAScript\b'
  '\bSBApplication\b'
  '\bNSUserAppleScriptTask\b'
  '\bNSUserUnixTask\b'
  '\bNSWorkspace\b'
  '\bWKWebView\b'
  '\bposix_spawn\b'
  'system\('
  '\bpopen\b'
  '^\s*import\s+ScriptingBridge\b'
  '^\s*import\s+WebKit\b'
  '^\s*import\s+Intents\b'
  '^\s*import\s+AppIntents\b'
)

FAIL=0
for pattern in "${PATTERNS[@]}"; do
  matches="$(grep -rnE "$pattern" Sources/ 2>/dev/null || true)"
  if [ -n "$matches" ]; then
    echo "FORBIDDEN: pattern '$pattern' found in Sources/:"
    echo "$matches" | sed 's/^/    /'
    FAIL=1
  fi
done

if [ "$FAIL" -ne 0 ]; then
  echo
  echo "lint-forbidden.sh: forbidden API reference found in Sources/ — see macos-bridge/README.md's" >&2
  echo "threat model for why these are banned. Missing functionality is never solved by adding one." >&2
  exit 1
fi

echo "lint-forbidden.sh: clean — no forbidden APIs in Sources/"
