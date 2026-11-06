#!/usr/bin/env bash
# Return one pinned, existing signing identity. Never create/export a key or
# silently change an established development identity back to ad-hoc signing.
set -euo pipefail
CONFIG="${YTRAY_DEV_SIGNING_CONFIG:-${HOME:?HOME is required}/Library/Application Support/YTrayBuild/dev-signing-identity}"
EXPLICIT="${YTRAY_DEV_SIGN_IDENTITY:-}"
if [[ "$EXPLICIT" == "-" ]]; then
    [[ "${YTRAY_DEV_ALLOW_ADHOC:-0}" == "1" ]] || { echo "Ad-hoc development signing loses TCC grants after rebuilding. Set YTRAY_DEV_ALLOW_ADHOC=1 to opt in." >&2; exit 1; }
    echo -
    exit 0
fi
IDENTITIES="$(security find-identity -v -p codesigning)"
SELECTED="$(printf '%s\n' "$IDENTITIES" | python3 -c '
import os, pathlib, re, sys
rows = re.findall(r"\b([A-Fa-f0-9]{40})\s+\"([^\"]+)\"", sys.stdin.read())
identities = {fingerprint.upper(): name for fingerprint, name in rows}
config = pathlib.Path(sys.argv[1])
explicit = sys.argv[2]
saved = config.read_text().strip() if config.is_file() else ""
wanted = explicit or saved
if wanted:
    matches = [f for f, name in identities.items() if f == wanted.upper() or name == wanted]
    if len(matches) != 1:
        raise SystemExit("Pinned signing identity is unavailable or ambiguous; supply YTRAY_DEV_SIGN_IDENTITY explicitly. Existing app was not changed.")
    print(matches[0])
elif len(identities) == 1:
    print(next(iter(identities)))
elif not identities:
    raise SystemExit("No valid code-signing identity. Install an Apple Development/Developer ID identity, or explicitly opt into ad-hoc signing. Existing app was not changed.")
else:
    raise SystemExit("Multiple signing identities. Set YTRAY_DEV_SIGN_IDENTITY to the chosen fingerprint; the choice will be pinned for later development builds.")
' "$CONFIG" "$EXPLICIT")"
umask 077
mkdir -p "$(dirname "$CONFIG")"
printf '%s\n' "$SELECTED" > "$CONFIG"
printf '%s\n' "$SELECTED"
