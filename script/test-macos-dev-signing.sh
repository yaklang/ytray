#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT
mkdir "$SCRATCH/bin"
cat > "$SCRATCH/bin/security" <<'SECURITY'
#!/bin/bash
cat "$YTRAY_TEST_IDENTITIES"
SECURITY
chmod +x "$SCRATCH/bin/security"
export PATH="$SCRATCH/bin:$PATH"
export YTRAY_TEST_IDENTITIES="$SCRATCH/identities"
export YTRAY_DEV_SIGNING_CONFIG="$SCRATCH/pinned-identity"
unset YTRAY_DEV_SIGN_IDENTITY YTRAY_DEV_ALLOW_ADHOC
printf ' 1) AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA "Developer ID: Fixture"\n 2) AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA "Developer ID: Fixture"\n' > "$YTRAY_TEST_IDENTITIES"
[[ "$("$ROOT/resolve-macos-dev-signing.sh")" == "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" ]]
printf ' 1) BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB "Developer ID: Other"\n 2) AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA "Developer ID: Fixture"\n' > "$YTRAY_TEST_IDENTITIES"
[[ "$("$ROOT/resolve-macos-dev-signing.sh")" == "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" ]]
printf ' 1) BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB "Developer ID: Other"\n' > "$YTRAY_TEST_IDENTITIES"
if "$ROOT/resolve-macos-dev-signing.sh" 2>/dev/null; then echo 'Missing pinned certificate was accepted' >&2; exit 1; fi
[[ "$(cat "$YTRAY_DEV_SIGNING_CONFIG")" == "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" ]]
if YTRAY_DEV_SIGN_IDENTITY=- "$ROOT/resolve-macos-dev-signing.sh" 2>/dev/null; then echo 'Silent ad-hoc downgrade was accepted' >&2; exit 1; fi
[[ "$(YTRAY_DEV_SIGN_IDENTITY=- YTRAY_DEV_ALLOW_ADHOC=1 "$ROOT/resolve-macos-dev-signing.sh")" == "-" ]]
[[ "$(YTRAY_DEV_SIGN_IDENTITY=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB "$ROOT/resolve-macos-dev-signing.sh")" == "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB" ]]
echo 'Development signing selection passed: duplicate identities, pinning, missing key, explicit migration, ad-hoc guard.'
