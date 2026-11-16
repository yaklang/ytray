#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT_DIR="${1:?usage: prepare-yakit-browser-agent.sh OUTPUT_DIR}"
exec node "$SCRIPT_DIR/prepare-yakit-browser-agent.mjs" "$OUTPUT_DIR"
