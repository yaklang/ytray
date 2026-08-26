#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE="$PROJECT_ROOT/assets/app-icon/YTray.png"
WINDOWS_ROOT="$PROJECT_ROOT/windows/src/Assets/Icons"
SITE_APP_ROOT="$PROJECT_ROOT/site/src/app"
SITE_PUBLIC_ROOT="$PROJECT_ROOT/site/public/icons"
SHOWCASE_SOURCE_ROOT="$PROJECT_ROOT/docs/images/v0.1.2"
SHOWCASE_OUTPUT_ROOT="$PROJECT_ROOT/docs/images/v0.1.4"

command -v magick >/dev/null 2>&1 || {
    echo "ImageMagick 7 is required to generate YTray icon assets (missing: magick)." >&2
    exit 1
}

test -f "$SOURCE" || {
    echo "Canonical app icon is missing: $SOURCE" >&2
    exit 1
}

dimensions="$(magick identify -format '%wx%h' "$SOURCE")"
test "$dimensions" = "1024x1024" || {
    echo "Canonical app icon must be 1024x1024, got $dimensions" >&2
    exit 1
}
source_hash="$(shasum -a 256 "$SOURCE" | awk '{print $1}')"
printf '%s  YTray.png\n' "$source_hash" > "$PROJECT_ROOT/assets/app-icon/YTray.png.sha256"

mkdir -p \
    "$WINDOWS_ROOT/png/app" \
    "$WINDOWS_ROOT/png/tray-on-light" \
    "$WINDOWS_ROOT/png/tray-on-dark" \
    "$SITE_PUBLIC_ROOT" \
    "$SHOWCASE_OUTPUT_ROOT"

render_png() {
    local size="$1"
    local output="$2"
    magick "$SOURCE" -filter Lanczos -resize "${size}x${size}" -strip \
        -define png:color-type=6 "$output"
}

app_sizes=(16 20 24 32 40 48 64 96 128 256 512 1024)
for size in "${app_sizes[@]}"; do
    output="$WINDOWS_ROOT/png/app/ytray-app-$size.png"
    if [[ "$size" -eq 1024 ]]; then
        cp "$SOURCE" "$output"
    else
        render_png "$size" "$output"
    fi
done

tray_sizes=(16 20 24 32 40 48 64)
for size in "${tray_sizes[@]}"; do
    cp "$WINDOWS_ROOT/png/app/ytray-app-$size.png" \
        "$WINDOWS_ROOT/png/tray-on-light/ytray-tray-on-light-$size.png"
    cp "$WINDOWS_ROOT/png/app/ytray-app-$size.png" \
        "$WINDOWS_ROOT/png/tray-on-dark/ytray-tray-on-dark-$size.png"
done

app_ico_inputs=()
for size in 16 20 24 32 40 48 64 96 128 256; do
    app_ico_inputs+=("$WINDOWS_ROOT/png/app/ytray-app-$size.png")
done
magick "${app_ico_inputs[@]}" "$WINDOWS_ROOT/ytray-app.ico"

tray_light_inputs=()
tray_dark_inputs=()
for size in "${tray_sizes[@]}"; do
    tray_light_inputs+=("$WINDOWS_ROOT/png/tray-on-light/ytray-tray-on-light-$size.png")
    tray_dark_inputs+=("$WINDOWS_ROOT/png/tray-on-dark/ytray-tray-on-dark-$size.png")
done
magick "${tray_light_inputs[@]}" "$WINDOWS_ROOT/ytray-tray-on-light.ico"
magick "${tray_dark_inputs[@]}" "$WINDOWS_ROOT/ytray-tray-on-dark.ico"

cp "$SOURCE" "$SITE_APP_ROOT/icon.png"
render_png 180 "$SITE_APP_ROOT/apple-icon.png"
render_png 192 "$SITE_PUBLIC_ROOT/icon-192.png"
render_png 512 "$SITE_PUBLIC_ROOT/icon-512.png"
magick \
    "$WINDOWS_ROOT/png/app/ytray-app-16.png" \
    "$WINDOWS_ROOT/png/app/ytray-app-32.png" \
    "$WINDOWS_ROOT/png/app/ytray-app-48.png" \
    "$SITE_APP_ROOT/favicon.ico"

# Preserve the versioned Windows captures while replacing their title-bar brand
# mark with the canonical v0.1.4 icon. The 27px placement mirrors ManagerView.xaml.
for source_image in "$SHOWCASE_SOURCE_ROOT"/ytray-windows-*.png; do
    output_image="$SHOWCASE_OUTPUT_ROOT/$(basename "$source_image")"
    if [[ "$(basename "$source_image")" == "ytray-windows-widget.png" ]]; then
        cp "$source_image" "$output_image"
        continue
    fi
    magick "$source_image" \
        -fill '#16191B' -draw 'rectangle 17,8 47,42' \
        \( "$SOURCE" -filter Lanczos -resize 27x27 \) \
        -geometry +18+12 -composite -strip "$output_image"
done

(
    cd "$WINDOWS_ROOT"
    magick -background none preview.svg preview.png
)

python3 "$SCRIPT_DIR/verify-app-icons.py"
echo "YTray app icons regenerated from assets/app-icon/YTray.png"
