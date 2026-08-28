#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TRASH_DATA=0
DRY_RUN=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --trash-data) TRASH_DATA=1 ;;
        --dry-run) DRY_RUN=1 ;;
        *) echo "Usage: $0 [--dry-run] [--trash-data]" >&2; exit 2 ;;
    esac
    shift
done

case "$(uname -m)" in
    arm64) ARCH_LABEL="arm64" ;;
    x86_64) ARCH_LABEL="amd64" ;;
    *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
esac

APP_PATH="$PROJECT_ROOT/dist/darwin-$ARCH_LABEL-dev/YTrayDev.app"
USER_ROOT="${HOME:?HOME is required}"
DATA_PATH="$USER_ROOT/Library/Application Support/YTrayDev"
TRASH_ROOT="$USER_ROOT/.Trash"
STAMP="$(date +%Y%m%d-%H%M%S)-$$"

if /usr/bin/pgrep -f "user-data-dir=$DATA_PATH/Profiles/" >/dev/null 2>&1; then
    echo "YTrayDev browser instances are still running. Stop them in YTrayDev before rollback." >&2
    exit 1
fi

if [[ "$DRY_RUN" -eq 1 ]]; then
    if [[ -e "$APP_PATH" ]]; then
        BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP_PATH/Contents/Info.plist")"
        [[ "$BUNDLE_ID" == "io.yaklang.ytray.dev" ]] || {
            echo "Unexpected app bundle: $APP_PATH ($BUNDLE_ID)" >&2
            exit 1
        }
    fi
    echo "Dry run passed. App target: $APP_PATH"
    echo "Dry run passed. Data target: $DATA_PATH (trash requested: $TRASH_DATA)"
    exit 0
fi

if /usr/bin/pgrep -f "$APP_PATH/Contents/MacOS/YTrayDev" >/dev/null 2>&1; then
    /usr/bin/osascript -e 'tell application id "io.yaklang.ytray.dev" to quit' >/dev/null 2>&1 || true
    for _ in {1..20}; do
        /usr/bin/pgrep -f "$APP_PATH/Contents/MacOS/YTrayDev" >/dev/null 2>&1 || break
        sleep 0.1
    done
fi

mkdir -p "$TRASH_ROOT"
if [[ -e "$APP_PATH" ]]; then
    BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP_PATH/Contents/Info.plist")"
    [[ "$BUNDLE_ID" == "io.yaklang.ytray.dev" ]] || {
        echo "Refusing to move an unexpected app bundle: $APP_PATH ($BUNDLE_ID)" >&2
        exit 1
    }
    mv "$APP_PATH" "$TRASH_ROOT/YTrayDev.app-$STAMP"
    echo "Moved YTrayDev.app to Trash (recoverable)."
fi

if [[ "$TRASH_DATA" -eq 1 && -e "$DATA_PATH" ]]; then
    [[ "$(basename "$DATA_PATH")" == "YTrayDev" ]] || {
        echo "Refusing unexpected data path: $DATA_PATH" >&2
        exit 1
    }
    mv "$DATA_PATH" "$TRASH_ROOT/YTrayDev-data-$STAMP"
    echo "Moved YTrayDev data to Trash (recoverable)."
else
    echo "Kept isolated YTrayDev data at: $DATA_PATH"
    echo "Run again with --trash-data to move it to Trash as well."
fi
