#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: release-notes.sh <version>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

awk -v heading="## $VERSION" '
  $0 == heading || index($0, heading " ") == 1 { found=1; next }
  found && /^## / { exit }
  found { print }
' "$ROOT/CHANGELOG.md" | sed '/./,$!d'
