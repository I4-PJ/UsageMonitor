#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

CONFIGURATION="${CONFIGURATION:-Release}"
FRAMEWORK="${FRAMEWORK:-net9.0}"
RUNTIME="${1:-${RUNTIME:-osx-arm64}}"
case "$RUNTIME" in
  osx-arm64|osx-x64) ;;
  *) echo "Runtime must be osx-arm64 or osx-x64." >&2; exit 1 ;;
esac
VERSION="${VERSION:-$(python3 -c 'import sys, xml.etree.ElementTree as ET; print(ET.parse(sys.argv[1]).findtext("./PropertyGroup/Version"))' "$PROJECT_DIR/UsageMonitor.csproj")}"
if [[ ! "$VERSION" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]]; then
  echo "Version must have the form 0.1.0 or 0.1.0-rc.1." >&2
  exit 1
fi
BUNDLE_VERSION="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"

PUBLISH_DIR="$PROJECT_DIR/artifacts/publish/$RUNTIME"
BUILD_DIR="$PROJECT_DIR/artifacts/build"
APP_DIR="$PROJECT_DIR/artifacts/macos/UsageMonitor.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

rm -rf -- "$PUBLISH_DIR" "$APP_DIR"

dotnet publish "$PROJECT_DIR/UsageMonitor.csproj" \
  --configuration "$CONFIGURATION" \
  --framework "$FRAMEWORK" \
  --runtime "$RUNTIME" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -p:Version="$VERSION" \
  --artifacts-path "$BUILD_DIR" \
  --output "$PUBLISH_DIR"

python3 "$SCRIPT_DIR/collect-licenses.py" \
  --assets "$BUILD_DIR/obj/UsageMonitor/project.assets.json" \
  --destination "$PUBLISH_DIR/ThirdPartyLicenses" \
  --runtime "$RUNTIME"

mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
cp -R "$PUBLISH_DIR"/. "$MACOS_DIR"/
cp "$PROJECT_DIR/Packaging/macOS/Info.plist" "$CONTENTS_DIR/Info.plist"
cp "$PROJECT_DIR/Assets/AppIcon.icns" "$RESOURCES_DIR/AppIcon.icns"
cp "$PROJECT_DIR"/Assets/AppIcon-*.png "$RESOURCES_DIR"/
cp "$PROJECT_DIR/Packaging/INSTALL-macOS.txt" "$RESOURCES_DIR/INSTALL.txt"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $BUNDLE_VERSION" "$CONTENTS_DIR/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUNDLE_VERSION" "$CONTENTS_DIR/Info.plist"
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
ditto -c -k --sequesterRsrc --keepParent "$APP_DIR" "$PROJECT_DIR/artifacts/UsageMonitor-$VERSION-$RUNTIME.zip"
echo "Created $APP_DIR"
