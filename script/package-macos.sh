#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PACKAGE_ROOT="$PROJECT_ROOT/darwin"
RESOURCE_ROOT="$PACKAGE_ROOT/Resources"
OUTPUT_ROOT="$PROJECT_ROOT/dist"
ICON_SOURCE="$PROJECT_ROOT/assets/app-icon/YTray.png"
INFO_PLIST_SOURCE="$RESOURCE_ROOT/Info.plist"

VERSION="$(tr -d '[:space:]' < "$PROJECT_ROOT/VERSION")"
REQUESTED_ARCH="native"
BUILD_DMG=0
DEVELOPMENT_BUILD=0
VERSION_WAS_REQUESTED=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) REQUESTED_ARCH="${2:?--arch requires arm64, amd64, or universal}"; shift 2 ;;
        --universal) REQUESTED_ARCH="universal"; shift ;;
        --version) VERSION="${2:?--version requires a version}"; VERSION_WAS_REQUESTED=1; shift 2 ;;
        --dev) DEVELOPMENT_BUILD=1; shift ;;
        --dmg) BUILD_DMG=1; shift ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
done

if [[ "$DEVELOPMENT_BUILD" -eq 1 && "$VERSION_WAS_REQUESTED" -eq 0 ]]; then
    VERSION="${VERSION}-dev"
fi

if [[ "$DEVELOPMENT_BUILD" -eq 1 && "$BUILD_DMG" -eq 1 ]]; then
    echo "YTrayDev is a local-only app bundle and cannot be packaged as a release DMG." >&2
    exit 1
fi

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z][0-9A-Za-z.-]*)?$ ]] || {
    echo "Invalid version: $VERSION" >&2
    exit 1
}

case "$REQUESTED_ARCH" in
    native)
        case "$(uname -m)" in
            arm64) ARCH_LABEL="arm64"; SWIFT_ARCHS=(--arch arm64) ;;
            x86_64) ARCH_LABEL="amd64"; SWIFT_ARCHS=(--arch x86_64) ;;
            *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
        esac
        ;;
    arm64) ARCH_LABEL="arm64"; SWIFT_ARCHS=(--arch arm64) ;;
    amd64|x86_64) ARCH_LABEL="amd64"; SWIFT_ARCHS=(--arch x86_64) ;;
    universal) ARCH_LABEL="universal"; SWIFT_ARCHS=(--arch arm64 --arch x86_64) ;;
    *) echo "Unsupported --arch value: $REQUESTED_ARCH" >&2; exit 1 ;;
esac

if [[ "$DEVELOPMENT_BUILD" -eq 1 ]]; then
    APP_NAME="YTrayDev"
    EXECUTABLE_NAME="YTrayDev"
    OUTPUT_FLAVOR="-dev"
    FLAVOR_LABEL="development"
else
    APP_NAME="YTray"
    EXECUTABLE_NAME="YTray"
    OUTPUT_FLAVOR=""
    FLAVOR_LABEL="production"
fi

APP_OUTPUT_ROOT="$OUTPUT_ROOT/darwin-$ARCH_LABEL$OUTPUT_FLAVOR"
APP_BUNDLE="$APP_OUTPUT_ROOT/$APP_NAME.app"
DMG_PATH="$OUTPUT_ROOT/YTray-$VERSION-darwin-$ARCH_LABEL.dmg"
ICONSET_DIR="$APP_OUTPUT_ROOT/YTray.iconset"
BUNDLED_EXTENSION_DIR="$APP_OUTPUT_ROOT/BundledExtension"

command -v magick >/dev/null 2>&1 || {
    echo "ImageMagick is required to render the app icon set (missing: magick)." >&2
    exit 1
}

rm -rf "$APP_OUTPUT_ROOT"
mkdir -p "$APP_OUTPUT_ROOT"
"$SCRIPT_DIR/prepare-yakit-browser-agent.sh" "$BUNDLED_EXTENSION_DIR"

BUILD_ARGS=(--package-path "$PACKAGE_ROOT" -c release "${SWIFT_ARCHS[@]}")
swift build "${BUILD_ARGS[@]}"
BIN_PATH="$(swift build "${BUILD_ARGS[@]}" --show-bin-path)"

mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources/BundledExtension" "$ICONSET_DIR"
cp "$BIN_PATH/YTray" "$APP_BUNDLE/Contents/MacOS/$EXECUTABLE_NAME"
cp "$BUNDLED_EXTENSION_DIR/yakit-browser-agent.zip" "$APP_BUNDLE/Contents/Resources/BundledExtension/"
cp "$BUNDLED_EXTENSION_DIR/bundled-extension.json" "$APP_BUNDLE/Contents/Resources/BundledExtension/"

BASE_PNG="$APP_OUTPUT_ROOT/YTrayAppIcon-1024.png"
cp "$ICON_SOURCE" "$BASE_PNG"

render_icon() {
    local size="$1" output="$2"
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
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP_BUNDLE/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${GITHUB_RUN_NUMBER:-$(git -C "$PROJECT_ROOT" rev-list --count HEAD)}" "$APP_BUNDLE/Contents/Info.plist"
if [[ "$DEVELOPMENT_BUILD" -eq 1 ]]; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleDisplayName YTrayDev" "$APP_BUNDLE/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleName YTrayDev" "$APP_BUNDLE/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleExecutable YTrayDev" "$APP_BUNDLE/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier io.yaklang.ytray.dev" "$APP_BUNDLE/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Add :YTrayInstanceColorThemes bool true" "$APP_BUNDLE/Contents/Info.plist"
fi

# Local packages are ad-hoc signed. The release workflow replaces this with a
# stable Developer ID signature and notarization when the repository secrets exist.
codesign --force --deep --sign - "$APP_BUNDLE"
codesign --verify --deep --strict "$APP_BUNDLE"

ARCHS="$(lipo -archs "$APP_BUNDLE/Contents/MacOS/$EXECUTABLE_NAME")"
case "$ARCH_LABEL:$ARCHS" in
    arm64:arm64|amd64:x86_64) ;;
    universal:*arm64*x86_64*|universal:*x86_64*arm64*) ;;
    *) echo "Packaged architecture mismatch: label=$ARCH_LABEL binary=$ARCHS" >&2; exit 1 ;;
esac

echo "app=$APP_BUNDLE"
echo "version=$VERSION"
echo "flavor=$FLAVOR_LABEL"
echo "architecture=$ARCH_LABEL"
echo "binary_archs=$ARCHS"
echo "bundled_extension=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$BUNDLED_EXTENSION_DIR/bundled-extension.json")"

if [[ "$BUILD_DMG" -eq 1 ]]; then
    "$SCRIPT_DIR/create-macos-dmg.sh" "$APP_BUNDLE" "$VERSION" "$ARCH_LABEL" "$DMG_PATH" >/dev/null
    echo "dmg=$DMG_PATH"
    shasum -a 256 "$DMG_PATH"
fi
