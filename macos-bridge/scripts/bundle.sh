#!/bin/bash
# Assemble and codesign ErdaBridge.app.
#
# NEVER ad-hoc sign (--sign -): TCC then falls back to the cdhash and every
# rebuild re-prompts and orphans the previous Reminders grant.
set -euo pipefail

cd "$(dirname "$0")/.."

IDENTITY="Apple Development: philipp.baum@me.com (397C268TA6)"
BUNDLE_ID="de.philippbaum.erdabridge"
APP=".build/ErdaBridge.app"

# The designated requirement decides what TCC and the Keychain ACL pin the app to.
#
# codesign's DEFAULT DR pins the leaf certificate's CN — including the
# "(397C268TA6)" suffix, which changes when the cert is renewed on 2027-03-24.
# That would silently re-prompt for Reminders and can hang a login-item start on
# a Keychain ACL prompt. So we default to the TEAM-ID-pinned DR, which is stable
# across cert renewals. Set ERDA_BRIDGE_DR=default to fall back to codesign's.
TEAM_ID="6CR38F5CRX"
TEAM_DR='=designated => identifier "'"$BUNDLE_ID"'" and anchor apple generic and certificate leaf[subject.OU] = "'"$TEAM_ID"'" and certificate 1[field.1.2.840.113635.100.6.2.1] /* exists */'

echo "==> swift build -c release --arch arm64"
swift build -c release --arch arm64

BIN="$(swift build -c release --arch arm64 --show-bin-path)/ErdaBridge"

echo "==> assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/ErdaBridge"
cp Resources/Info.plist "$APP/Contents/Info.plist"

echo "==> codesign"
if [ "${ERDA_BRIDGE_DR:-team}" = "team" ]; then
  echo "    (team-ID-pinned designated requirement)"
  codesign --force \
    --sign "$IDENTITY" \
    --identifier "$BUNDLE_ID" \
    --options runtime \
    --timestamp \
    --generate-entitlement-der \
    --entitlements Resources/ErdaBridge.entitlements \
    --requirements "$TEAM_DR" \
    "$APP"
else
  codesign --force \
    --sign "$IDENTITY" \
    --identifier "$BUNDLE_ID" \
    --options runtime \
    --timestamp \
    --generate-entitlement-der \
    --entitlements Resources/ErdaBridge.entitlements \
    "$APP"
fi

echo "==> codesign --verify --strict --deep"
codesign --verify --strict --deep --verbose=4 "$APP"

echo "==> designated requirement"
codesign -d -r- --verbose=4 "$APP"

echo "==> cdhash"
codesign -dvvv "$APP" 2>&1 | grep -E '^(CDHash|Identifier|TeamIdentifier|Signature|Timestamp)'

echo "==> otool -L"
otool -L "$APP/Contents/MacOS/ErdaBridge"

# Undefined-symbol scan for the forbidden APIs — a linked-in dependency shows up here even if
# nothing in our own Sources/ mentions it by name (scripts/lint-forbidden.sh only greps our
# source). This is what would catch a transitive package pulling in a scripting bridge or WebKit.
echo "==> nm -u | grep forbidden-symbol patterns"
FORBIDDEN_SYMBOLS='NSTask|OSAScript|NSAppleScript|SBApplication|WKWebView|NSUserAppleScriptTask|NSUserUnixTask|posix_spawn'
if nm -u "$APP/Contents/MacOS/ErdaBridge" 2>/dev/null | grep -Ei "$FORBIDDEN_SYMBOLS"; then
  echo "!! forbidden symbol found in the linked binary — see the grep output above" >&2
  exit 1
else
  echo "    clean — no NSTask/OSAScript/WKWebView-style undefined symbols"
fi

echo "==> done: $APP"
