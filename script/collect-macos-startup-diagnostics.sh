#!/usr/bin/env bash
# Read-only collection for a Mac where YTray disappears at startup.
# Does not launch YTray, change preferences, or copy browser profiles/state.json.
set -euo pipefail

APP_PATH="${1:-/Applications/YTray.app}"
OUTPUT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ytray-startup-diagnostics.XXXXXX")"
REPORT="$OUTPUT_DIR/system.txt"

{
    date -u
    sw_vers
    uname -m
    echo "app=$APP_PATH"
    /usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP_PATH/Contents/Info.plist" || true
    /usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$APP_PATH/Contents/Info.plist" || true
    echo 'Running YTray processes:'
    pgrep -lx YTray || true
    echo 'Signature:'
    codesign -dv --verbose=2 "$APP_PATH" || true
    codesign --verify --deep --strict "$APP_PATH" || true
    echo 'Quarantine attribute:'
    xattr -p com.apple.quarantine "$APP_PATH" || true
} > "$REPORT" 2>&1

for filename in ytray.log ytray-errors.log; do
    log_file="$HOME/Library/Application Support/YTray/Logs/$filename"
    if [[ -f "$log_file" ]]; then
        tail -n 500 "$log_file" > "$OUTPUT_DIR/$filename"
    fi
done

mkdir -p "$OUTPUT_DIR/crashes"
while IFS= read -r crash_file; do
    cp "$crash_file" "$OUTPUT_DIR/crashes/"
done < <(find "$HOME/Library/Logs/DiagnosticReports" /Library/Logs/DiagnosticReports \
    -maxdepth 1 -type f -name 'YTray*' -mtime -7 -print 2>/dev/null)

echo "Diagnostics saved to: $OUTPUT_DIR"
echo 'Review the files before sharing; logs and crash reports can contain local paths and URLs.'
