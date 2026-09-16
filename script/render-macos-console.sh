#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="${1:?usage: render-macos-console.sh OUTPUT_DIR [YTRAY_BINARY]}"
if [[ $# -ge 2 ]]; then
    YTRAY_BINARY="$2"
else
    YTRAY_BINARY="$(swift build --package-path "$PROJECT_ROOT/darwin" --configuration release --show-bin-path)/YTray"
fi
mkdir -p "$OUTPUT_DIR"

for page in quick runtimes settings instances plugins launchAtLogin; do
    "$YTRAY_BINARY" --render-manager "$OUTPUT_DIR/ytray-console-light-$page.png" "$page" --sample-data --light
    "$YTRAY_BINARY" --render-manager "$OUTPUT_DIR/ytray-console-compact-$page.png" "$page" --sample-data --light --compact
done
for page in settings runtimes; do
    "$YTRAY_BINARY" --render-manager "$OUTPUT_DIR/ytray-console-dark-$page.png" "$page" --sample-data
done
for step in 0 1 2 3; do
    "$YTRAY_BINARY" --render-wizard "$OUTPUT_DIR/ytray-console-wizard-$step.png" --sample-data --light --wizard-step "$step"
done
"$YTRAY_BINARY" --render-wizard "$OUTPUT_DIR/ytray-console-wizard-dark-1.png" --sample-data --wizard-step 1

for screenshot in "$OUTPUT_DIR"/ytray-console-*.png; do
    test -s "$screenshot"
    sips -g pixelWidth -g pixelHeight "$screenshot" >/dev/null
done
echo "Rendered all six console pages, minimum window layouts, dark controls and all four wizard steps."
