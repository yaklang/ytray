#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PACKAGE_ROOT="$PROJECT_ROOT/darwin"
RESOURCE_ROOT="$PACKAGE_ROOT/Resources"
OUTPUT_ROOT="$PROJECT_ROOT/dist"
APP_BUNDLE="$OUTPUT_ROOT/YTray.app"
DMG_PATH="$OUTPUT_ROOT/YTray.dmg"
ICON_SOURCE="$RESOURCE_ROOT/YTrayAppIcon.svg"
INFO_PLIST_SOURCE="$RESOURCE_ROOT/Info.plist"
ICONSET_DIR="$OUTPUT_ROOT/YTray.iconset"

BUILD_UNIVERSAL=0
BUILD_DMG=0
for arg in "$@"; do
    case "$arg" in
        --universal) BUILD_UNIVERSAL=1 ;;
        --dmg) BUILD_DMG=1 ;;
        *) echo "Unknown option: $arg (supported: --universal, --dmg)" >&2; exit 1 ;;
    esac
done

if ! command -v magick >/dev/null 2>&1; then
    echo "ImageMagick is required to render the SVG app icon (missing: magick)." >&2
    exit 1
fi

BUILD_ARGS=(--package-path "$PACKAGE_ROOT" -c release)
if [ "$BUILD_UNIVERSAL" -eq 1 ]; then
    BUILD_ARGS+=(--arch arm64 --arch x86_64)
fi
swift build "${BUILD_ARGS[@]}"
# Universal builds land outside .build/release, so always resolve the binary path
# from SwiftPM itself instead of hard-coding the native layout.
BIN_PATH="$(swift build "${BUILD_ARGS[@]}" --show-bin-path)"
mkdir -p "$OUTPUT_ROOT"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources" "$ICONSET_DIR"

cp "$BIN_PATH/YTray" "$APP_BUNDLE/Contents/MacOS/YTray"

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
cp "$INFO_PLIST_SOURCE" "$APP_BUNDLE/Info.plist"

codesign --force --deep --sign - "$APP_BUNDLE"
echo "$APP_BUNDLE"

if [ "$BUILD_UNIVERSAL" -eq 1 ]; then
    ARCHS="$(lipo -archs "$APP_BUNDLE/Contents/MacOS/YTray")"
    case "$ARCHS" in
        *arm64*x86_64*|*x86_64*arm64*) ;;
        *) echo "Universal build expected arm64 + x86_64 but got: $ARCHS" >&2; exit 1 ;;
    esac
    echo "universal archs: $ARCHS"
fi

if [ "$BUILD_DMG" -eq 1 ]; then
    STAGING_DIR="$(mktemp -d)"
    trap 'rm -rf "$STAGING_DIR"' EXIT
    cp -R "$APP_BUNDLE" "$STAGING_DIR/YTray.app"
    ln -s /Applications "$STAGING_DIR/Applications"
    rm -f "$DMG_PATH"
    hdiutil create -volname "YTray" -srcfolder "$STAGING_DIR" -format UDZO -ov "$DMG_PATH" >/dev/null
    echo "$DMG_PATH"
    shasum -a 256 "$DMG_PATH"
fi
