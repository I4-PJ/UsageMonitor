#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

CONFIGURATION="${CONFIGURATION:-Release}"
FRAMEWORK="${FRAMEWORK:-net9.0}"
RUNTIME="${1:-${RUNTIME:-osx-arm64}}"

PUBLISH_DIR="$PROJECT_DIR/artifacts/publish/$RUNTIME"
APP_DIR="$PROJECT_DIR/artifacts/macos/UsageMonitor.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

rm -rf "$PUBLISH_DIR" "$APP_DIR"

dotnet publish "$PROJECT_DIR/UsageMonitor.csproj" \
  --configuration "$CONFIGURATION" \
  --framework "$FRAMEWORK" \
  --runtime "$RUNTIME" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "$PUBLISH_DIR"

mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
cp -R "$PUBLISH_DIR"/. "$MACOS_DIR"/
cp "$PROJECT_DIR/Packaging/macOS/Info.plist" "$CONTENTS_DIR/Info.plist"
cp "$PROJECT_DIR/Assets/AppIcon.icns" "$RESOURCES_DIR/AppIcon.icns"
cp "$PROJECT_DIR"/Assets/AppIcon-*.png "$RESOURCES_DIR"/
printf 'APPL????' > "$CONTENTS_DIR/PkgInfo"
mv "$MACOS_DIR/UsageMonitor" "$MACOS_DIR/UsageMonitor.real"
printf '%s\n' \
  '#!/bin/sh' \
  'DIR=${0%/*}' \
  'REAL="$DIR/UsageMonitor.real"' \
  'if pgrep -qx UsageMonitor.real >/dev/null 2>&1; then' \
  '  exit 0' \
  'fi' \
  'cd "$DIR" || exit 1' \
  'nohup "$REAL" "$@" >/dev/null 2>&1 &' \
  'exit 0' \
  > "$MACOS_DIR/UsageMonitor"
chmod +x "$MACOS_DIR/UsageMonitor"
chmod +x "$MACOS_DIR/UsageMonitor.real"
xattr -cr "$APP_DIR" 2>/dev/null || true

touch "$APP_DIR"
echo "Created $APP_DIR"
