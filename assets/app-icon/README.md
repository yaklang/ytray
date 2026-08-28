# YTray application icon

`YTray.png` is the canonical material artwork for YTray application surfaces.
It is a 1024 × 1024 RGBA PNG and is the only source that should be replaced for
future application-icon updates.

Run the following command from the repository root after replacing it:

```bash
./script/generate-app-icons.sh
```

The generator produces the macOS packaging input, Windows application and tray
PNG/ICO sets, website favicon/Apple/PWA icons, and the versioned website
showcase images. `script/verify-app-icons.py` checks their dimensions, ICO
frames, source hash, and repository references in CI.

The website identity system additionally uses the approved flat PNG and vector
SVG in `site/public/brand`: flat artwork for the hero, vector artwork for compact
navigation/footer marks, and this material artwork for application/download
surfaces. Those website-only assets do not change packaged application icons.

The generator refreshes `YTray.png.sha256`; CI uses that digest to catch source
changes that were not followed by regeneration.
