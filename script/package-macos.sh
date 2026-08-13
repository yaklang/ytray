#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PACKAGE_ROOT="$PROJECT_ROOT/darwin"
RESOURCE_ROOT="$PACKAGE_ROOT/Resources"
OUTPUT_ROOT="$PROJECT_ROOT/dist"
APP_BUNDLE="$OUTPUT_ROOT/YTray.app"
ICON_SOURCE="$RESOURCE_ROOT/YTrayAppIcon.svg"
INFO_PLIST_SOURCE="$RESOURCE_ROOT/Info.plist"
ICONSET_DIR="$OUTPUT_ROOT/YTray.iconset"

if ! command -v magick >/dev/null 2>&1; then
    echo "ImageMagick is required to render the SVG app icon (missing: magick)." >&2
    exit 1
fi

swift build --package-path "$PACKAGE_ROOT" -c release

mkdir -p "$OUTPUT_ROOT"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources" "$ICONSET_DIR"

cp "$PACKAGE_ROOT/.build/release/YTray" "$APP_BUNDLE/Contents/MacOS/YTray"

BASE_PNG="$OUTPUT_ROOT/YTrayAppIcon-1024.png"
magick -background none "$ICON_SOURCE" -resize 1024x1024 "$BASE_PNG"

render_icon() {
    local size="$1"
    local output="$2"
    magick "$BASE_PNG" -filter Lanczos -resize "${size}x${size}" "$ICONSET_DIR/$output"
}

render_icon 16 icon_16x16.png
render_icon 32 icon_16x16@2x.png
render_icon 32 icon_32x32.png
render_icon 64 icon_32x32@2x.png
render_icon 128 icon_128x128.png
render_icon 256 icon_128x128@2x.png
render_icon 256 icon_256x256.png
render_icon 512 icon_256x256@2x.png
render_icon 512 icon_512x512.png
cp "$BASE_PNG" "$ICONSET_DIR/icon_512x512@2x.png"

iconutil -c icns "$ICONSET_DIR" -o "$APP_BUNDLE/Contents/Resources/YTray.icns"
cp "$INFO_PLIST_SOURCE" "$APP_BUNDLE/Contents/Info.plist"

codesign --force --deep --sign - "$APP_BUNDLE"
echo "$APP_BUNDLE"
