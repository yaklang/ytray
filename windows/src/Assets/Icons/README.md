# YTray icon assets

This directory contains the production icon system based on the selected
"runtime hub + three managed instances + broken orbit" concept.

## Source files

- `ytray-app-icon.svg`: full-color Windows application/desktop icon.
- `ytray-tray-on-light.svg`: dark monochrome tray glyph for light taskbars.
- `ytray-tray-on-dark.svg`: light monochrome tray glyph for dark taskbars.
- `preview.svg` and `preview.png`: review sheet for the exported icon set.

## Generated files

- `png/app/`: transparent application PNGs at 16, 20, 24, 32, 40, 48, 64,
  96, 128, 256, 512, and 1024 pixels.
- `png/tray-on-light/`: transparent dark tray glyphs at 16, 20, 24, 32, 40,
  48, and 64 pixels.
- `png/tray-on-dark/`: transparent light tray glyphs at the same sizes.
- `ytray-app.ico`: multi-frame Windows application icon containing 16 through
  256 pixel frames.
- `ytray-tray-on-light.ico` and `ytray-tray-on-dark.ico`: multi-frame tray
  resources containing 16 through 64 pixel frames.

The SVG files are the editable masters. Regenerate raster assets from the SVG
masters instead of editing individual PNG or ICO frames.
