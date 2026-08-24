#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: prepare-release-index.sh <version> <manifest.json> [output-dir]}"
MANIFEST="${2:?usage: prepare-release-index.sh <version> <manifest.json> [output-dir]}"
OUT_DIR="${3:-release-index}"
PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-https://aliyun-oss.yaklang.com/ytray}"
RELEASES_URL="${RELEASES_URL:-${PUBLIC_BASE_URL}/releases.json}"
VERSION_RE='^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z][0-9A-Za-z.-]*)?$'

die() { echo "ERROR: $*" >&2; exit 1; }

[[ "$VERSION" =~ $VERSION_RE ]] || die "invalid version: $VERSION"
[[ -s "$MANIFEST" ]] || die "manifest is missing: $MANIFEST"
jq -e --arg version "$VERSION" --arg base "$PUBLIC_BASE_URL/$VERSION/" '
  .schema_version == 1 and .product == "ytray" and .version == $version and
  (.plugin.version | type == "string" and length > 0) and
  (.assets | type == "array" and length == 4) and
  ([.assets[] | .platform + ":" + .architecture] | unique | length == 4) and
  all(.assets[];
    (.platform == "darwin" or .platform == "windows") and
    (.architecture == "arm64" or .architecture == "amd64" or .architecture == "386") and
    (.url | startswith($base)) and
    (.sha256 | test("^[a-f0-9]{64}$"))
  )
' "$MANIFEST" >/dev/null || die "manifest schema validation failed"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

if [[ -n "${EXISTING_RELEASES_FILE:-}" ]]; then
  cp "$EXISTING_RELEASES_FILE" "$OUT_DIR/releases.previous.json"
else
  status="$(curl --retry 3 --retry-all-errors -sS -L -o "$OUT_DIR/releases.previous.json" -w '%{http_code}' "$RELEASES_URL")" ||
    die "failed to fetch existing release index"
  case "$status" in
    200) ;;
    404) printf '%s\n' '{"schema_version":1,"product":"ytray","latest":"","versions":[]}' > "$OUT_DIR/releases.previous.json" ;;
    *) die "unexpected HTTP $status while fetching release index" ;;
  esac
fi

jq -e '.schema_version == 1 and .product == "ytray" and (.versions | type == "array")' \
  "$OUT_DIR/releases.previous.json" >/dev/null || die "existing releases index is invalid"

jq --slurpfile release "$MANIFEST" --arg version "$VERSION" '
  .latest = $version |
  .versions = ([$release[0]] + [.versions[] | select(.version != $version)])
' "$OUT_DIR/releases.previous.json" > "$OUT_DIR/releases.json"

cp "$MANIFEST" "$OUT_DIR/latest.json"
printf '%s\n' "$VERSION" > "$OUT_DIR/latest.txt"
printf '%s\n' "$VERSION" > "$OUT_DIR/latest-version.txt"
jq -e --arg version "$VERSION" '.latest == $version and .versions[0].version == $version' \
  "$OUT_DIR/releases.json" >/dev/null

echo "Prepared YTray release index for $VERSION"
