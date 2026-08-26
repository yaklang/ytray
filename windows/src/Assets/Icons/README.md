# YTray icon assets

This directory contains generated Windows exports of the canonical YTray
artwork in `assets/app-icon/YTray.png`.

## Canonical source

- `../../../../assets/app-icon/YTray.png`: full-color 1024px RGBA master.
- `preview.svg` and `preview.png`: review sheet for the generated icon set.

## Generated files

- `png/app/`: transparent application PNGs at 16, 20, 24, 32, 40, 48, 64,
  96, 128, 256, 512, and 1024 pixels.
- `png/tray-on-light/` and `png/tray-on-dark/`: full-color brand exports at
  16, 20, 24, 32, 40, 48, and 64 pixels. Both theme variants intentionally
  use the same artwork so the application identity remains consistent.
- `ytray-app.ico`: multi-frame Windows application icon containing 16 through
  256 pixel frames.
- `ytray-tray-on-light.ico` and `ytray-tray-on-dark.ico`: multi-frame tray
  resources containing 16 through 64 pixel frames.

Run `./script/generate-app-icons.sh` from the repository root instead of
editing individual PNG or ICO frames.
