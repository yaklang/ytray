#!/usr/bin/env bash
set -euo pipefail

OUTPUT_DIR="${1:?usage: prepare-yakit-browser-agent.sh OUTPUT_DIR}"
MANIFEST_URL="https://aliyun-oss.yaklang.com/chrome-extension/manifest.json"

command -v python3 >/dev/null 2>&1 || {
    echo "python3 is required to resolve the latest Yakit Browser Agent release" >&2
    exit 1
}

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT
MANIFEST="$TEMP_DIR/manifest.json"
ARCHIVE="$TEMP_DIR/yakit-browser-agent.zip"

curl --fail --location --compressed --retry 3 --retry-all-errors --silent --show-error \
    "$MANIFEST_URL" --output "$MANIFEST"
METADATA="$(python3 - "$MANIFEST" <<'PY'
import json
import re
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    manifest = json.load(stream)

version = manifest.get("latest", "")
if not re.fullmatch(r"[0-9]+(?:\.[0-9]+)*", version):
    raise SystemExit("OSS manifest has an invalid latest version")
release = next((item for item in manifest.get("versions", []) if item.get("version") == version), None)
if release is None:
    raise SystemExit(f"OSS manifest does not describe latest version {version}")
artifact = next((item for item in release.get("artifacts", []) if item.get("variant") == "chrome-enterprise"), None)
if artifact is None:
    raise SystemExit(f"Yakit Browser Agent {version} has no chrome-enterprise artifact")

url = artifact.get("url", "")
sha256 = artifact.get("sha256", "")
size = artifact.get("size")
expected_prefix = f"https://aliyun-oss.yaklang.com/chrome-extension/{version}/"
if not url.startswith(expected_prefix) or "\n" in url or "\r" in url:
    raise SystemExit("OSS manifest contains an unexpected artifact URL")
if not re.fullmatch(r"[0-9a-fA-F]{64}", sha256):
    raise SystemExit("OSS manifest contains an invalid SHA-256")
if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
    raise SystemExit("OSS manifest contains an invalid artifact size")

print(version)
print(url)
print(sha256.lower())
print(size)
PY
)"
VERSION="$(printf '%s\n' "$METADATA" | sed -n '1p')"
URL="$(printf '%s\n' "$METADATA" | sed -n '2p')"
SHA256="$(printf '%s\n' "$METADATA" | sed -n '3p')"
SIZE="$(printf '%s\n' "$METADATA" | sed -n '4p')"

echo "Resolved latest Yakit Browser Agent: $VERSION"
curl --fail --location --compressed --retry 3 --retry-all-errors --silent --show-error \
    "$URL" --output "$ARCHIVE"
ACTUAL_SIZE="$(wc -c < "$ARCHIVE" | tr -d '[:space:]')"
[[ "$ACTUAL_SIZE" == "$SIZE" ]] || {
    echo "Yakit Browser Agent size mismatch: expected $SIZE, got $ACTUAL_SIZE" >&2
    exit 1
}
ACTUAL_SHA256="$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')"
NORMALIZED_ACTUAL_SHA256="$(printf '%s' "$ACTUAL_SHA256" | tr '[:upper:]' '[:lower:]')"
[[ "$NORMALIZED_ACTUAL_SHA256" == "$SHA256" ]] || {
    echo "Yakit Browser Agent SHA-256 mismatch" >&2
    exit 1
}
# Do not use grep -q here: with pipefail enabled it closes the pipe after the
# first match, causing unzip to receive SIGPIPE and a valid archive to fail.
unzip -Z1 "$ARCHIVE" | grep -E '^(manifest[.]json|[^/]+/manifest[.]json)$' >/dev/null || {
    echo "Yakit Browser Agent archive does not contain a supported manifest.json root" >&2
    exit 1
}
while IFS= read -r entry; do
    [[ "$entry" != /* && "$entry" != *\\* && "/$entry/" != *"/../"* ]] || {
        echo "Yakit Browser Agent archive contains an unsafe path: $entry" >&2
        exit 1
    }
done < <(unzip -Z1 "$ARCHIVE")

mkdir -p "$OUTPUT_DIR"
cp "$ARCHIVE" "$OUTPUT_DIR/yakit-browser-agent.zip"
printf '{\n  "version": "%s",\n  "sha256": "%s",\n  "size": %s,\n  "variant": "chrome-enterprise",\n  "sourceManifest": "%s"\n}\n' \
    "$VERSION" "$SHA256" "$SIZE" "$MANIFEST_URL" > "$OUTPUT_DIR/bundled-extension.json"
echo "Prepared Yakit Browser Agent $VERSION ($SIZE bytes) in $OUTPUT_DIR"
