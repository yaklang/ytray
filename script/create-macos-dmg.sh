#!/usr/bin/env bash
set -euo pipefail

APP_PATH="${1:?usage: create-macos-dmg.sh <app> <version> <architecture> [output]}"
VERSION="${2:?version is required}"
ARCHITECTURE="${3:?architecture is required}"
OUTPUT="${4:-dist/YTray-$VERSION-darwin-$ARCHITECTURE.dmg}"

[[ -d "$APP_PATH" && -x "$APP_PATH/Contents/MacOS/YTray" ]] || {
    echo "Invalid YTray app bundle: $APP_PATH" >&2
    exit 1
}

STAGING_DIR="$(mktemp -d)"
cleanup() { rm -rf "$STAGING_DIR"; }
trap cleanup EXIT

mkdir -p "$(dirname "$OUTPUT")"
ditto "$APP_PATH" "$STAGING_DIR/YTray.app"
ln -s /Applications "$STAGING_DIR/Applications"
rm -f "$OUTPUT"
hdiutil create -volname "YTray $VERSION" -srcfolder "$STAGING_DIR" -format UDZO -ov "$OUTPUT" >/dev/null
hdiutil verify "$OUTPUT" >/dev/null
echo "$OUTPUT"
shasum -a 256 "$OUTPUT"
