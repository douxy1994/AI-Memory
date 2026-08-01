#!/usr/bin/env bash
# AI Memory
# Copyright © 2026 douxy1994
# SPDX-License-Identifier: AGPL-3.0-only
#
set -euo pipefail

MODE="${1:-run}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="Debug"

if [[ "$MODE" == "--release" || "$MODE" == "release" ]]; then
  CONFIGURATION="Release"
  MODE="run"
fi

DERIVED_DATA="$ROOT_DIR/.build/DerivedData"
APP_BUNDLE="$DERIVED_DATA/Build/Products/$CONFIGURATION/AIMemory.app"
APP_BINARY="$APP_BUNDLE/Contents/MacOS/AIMemory"

build_app() {
  xcodebuild \
    -project "$ROOT_DIR/AIMemory.xcodeproj" \
    -scheme AIMemory \
    -configuration "$CONFIGURATION" \
    -derivedDataPath "$DERIVED_DATA" \
    CODE_SIGNING_ALLOWED=NO \
    build
}

stop_running_app() {
  pkill -x AIMemory >/dev/null 2>&1 || true
  # LaunchServices can return -600 when the previous instance is still
  # terminating. Wait briefly so the replacement always starts as the only
  # active instance.
  for _ in {1..30}; do
    if ! pgrep -x AIMemory >/dev/null 2>&1; then
      return
    fi
    sleep 0.1
  done
}

open_app() {
  # A just-terminated GUI process can leave a short-lived LaunchServices
  # registration behind and make the first `open` return -600. Retrying the
  # same bundle is safe because the app itself prohibits multiple instances.
  for _ in {1..5}; do
    if /usr/bin/open "$APP_BUNDLE"; then
      return
    fi
    sleep 0.2
  done
  return 1
}

build_app
stop_running_app

case "$MODE" in
  run)
    open_app
    ;;
  --debug|debug)
    lldb -- "$APP_BINARY"
    ;;
  --logs|logs)
    open_app
    /usr/bin/log stream --info --style compact \
      --predicate 'process == "AIMemory"'
    ;;
  --telemetry|telemetry)
    open_app
    /usr/bin/log stream --info --style compact \
      --predicate 'subsystem == "com.aimemory.app"'
    ;;
  --verify|verify)
    open_app
    sleep 2
    pgrep -x AIMemory >/dev/null
    ;;
  *)
    echo "usage: $0 [run|--release|--debug|--logs|--telemetry|--verify]" >&2
    exit 2
    ;;
esac
