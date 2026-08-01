#!/usr/bin/env bash
# AI Memory
# Copyright © 2026 douxy1994
# SPDX-License-Identifier: AGPL-3.0-only
#
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/AIMemory.xcodeproj"
DERIVED_DATA="$ROOT/.build/ReleaseDerivedData"
PRODUCTS="$DERIVED_DATA/Build/Products/Release"
BUILT_APP="$PRODUCTS/AIMemory.app"
DIST="$ROOT/release/AIMemory"
STAGING="$DIST/staging"
APP="$STAGING/AI Memory.app"
VERSION="${AIMEMORY_RELEASE_VERSION:-0.1.0}"
DMG="$DIST/AI-Memory-${VERSION}-macOS-universal.dmg"
CHECKSUM="$DMG.sha256"
SIGN_IDENTITY="${AIMEMORY_SIGN_IDENTITY:-Pot Local Code Signing}"
EXPECTED_REQUIREMENT='identifier "com.aimemory.app" and certificate leaf = H"a493ef6f181ec595f5216b01a4e2008778c4a592"'

if [[ "$DIST" != "$ROOT/release/AIMemory" ]]; then
  echo "Refusing to clean unexpected release directory: $DIST" >&2
  exit 1
fi

verify_signature() {
  local app="$1"
  local requirement
  codesign --verify --deep --strict "$app"
  requirement="$(codesign -d -r- "$app" 2>&1 | sed -n 's/^designated => //p')"
  if [[ "$requirement" != "$EXPECTED_REQUIREMENT" ]]; then
    echo "Unexpected AI Memory signing requirement:" >&2
    echo "  $requirement" >&2
    echo "Expected:" >&2
    echo "  $EXPECTED_REQUIREMENT" >&2
    exit 1
  fi
}

security find-identity -v -p codesigning \
  | grep -F "\"$SIGN_IDENTITY\"" >/dev/null

xcodebuild \
  -project "$PROJECT" \
  -scheme AIMemory \
  -configuration Release \
  -destination "generic/platform=macOS" \
  -derivedDataPath "$DERIVED_DATA" \
  ARCHS="arm64 x86_64" \
  ONLY_ACTIVE_ARCH=NO \
  CODE_SIGNING_ALLOWED=NO \
  clean build

rm -rf "$DIST"
mkdir -p "$STAGING"
ditto "$BUILT_APP" "$APP"

codesign --force --timestamp=none --sign "$SIGN_IDENTITY" \
  "$APP/Contents/Helpers/aimemory-mcp"
codesign --force --deep --timestamp=none --sign "$SIGN_IDENTITY" \
  --entitlements "$ROOT/AIMemory/AIMemory.entitlements" \
  "$APP"
verify_signature "$APP"

binary="$APP/Contents/MacOS/AIMemory"
architectures="$(lipo -archs "$binary")"
if [[ "$architectures" != *"arm64"* || "$architectures" != *"x86_64"* ]]; then
  echo "Release binary is not universal: $architectures" >&2
  exit 1
fi

if find "$APP" \
  \( -iname '*.db' -o -iname '*.sqlite*' -o -iname 'settings.json' \
     -o -iname 'credentials.json' -o -iname '.env' -o -iname '*.pem' \
     -o -iname '*.key' \) \
  | grep -q .; then
  echo "Release app contains user data, configuration, or key material." >&2
  exit 1
fi

if rg -Il \
  --glob '*.{json,plist,txt,md,strings,env,pem,key}' \
  '(BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|gh[pousr]_[A-Za-z0-9]{20,})' \
  "$APP" | grep -q .; then
  echo "Release app contains material matching a private credential pattern." >&2
  exit 1
fi

ln -s /Applications "$STAGING/Applications"
hdiutil create \
  -volname "AI Memory" \
  -srcfolder "$STAGING" \
  -ov \
  -format UDZO \
  "$DMG" >/dev/null
hdiutil verify "$DMG" >/dev/null

(
  cd "$DIST"
  shasum -a 256 "$(basename "$DMG")" > "$(basename "$CHECKSUM")"
)

rm -rf "$STAGING"
printf '%s\n' "$DMG" "$CHECKSUM"
