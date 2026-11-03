#!/usr/bin/env bash
# Downloads and verifies the latest Yakit Browser Agent into the Electron
# resources directory, reusing the cross-platform packager used by macOS.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUTPUT="$ROOT/linux/resources/bundled-extension"

exec bash "$ROOT/script/prepare-yakit-browser-agent.sh" "$OUTPUT"
