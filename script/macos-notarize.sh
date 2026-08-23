#!/usr/bin/env bash
set -euo pipefail

TARGET="${1:?usage: macos-notarize.sh <app-or-dmg>}"
[[ -e "$TARGET" ]] || { echo "Target not found: $TARGET" >&2; exit 1; }
: "${APPLE_ID:?APPLE_ID is required}"
: "${APPLE_APP_PASSWORD:?APPLE_APP_PASSWORD is required}"
: "${APPLE_TEAM_ID:?APPLE_TEAM_ID is required}"

submit() {
    xcrun notarytool submit "$1" --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" \
        --team-id "$APPLE_TEAM_ID" --wait
}

case "$TARGET" in
    *.app)
        ZIP_DIR="$(mktemp -d)"
        trap 'rm -rf "$ZIP_DIR"' EXIT
        ZIP="$ZIP_DIR/YTray.zip"
        ditto -c -k --keepParent "$TARGET" "$ZIP"
        submit "$ZIP"
        xcrun stapler staple "$TARGET"
        xcrun stapler validate "$TARGET"
        ;;
    *.dmg)
        submit "$TARGET"
        xcrun stapler staple "$TARGET"
        xcrun stapler validate "$TARGET"
        ;;
    *) echo "Unsupported notarization target: $TARGET" >&2; exit 1 ;;
esac
