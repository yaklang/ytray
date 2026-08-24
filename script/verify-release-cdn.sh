#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: verify-release-cdn.sh <version> <dist-dir> <index-dir>}"
DIST_DIR="${2:?usage: verify-release-cdn.sh <version> <dist-dir> <index-dir>}"
INDEX_DIR="${3:?usage: verify-release-cdn.sh <version> <dist-dir> <index-dir>}"
PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-https://aliyun-oss.yaklang.com/ytray}"
MAX_ATTEMPTS="${CDN_VERIFY_ATTEMPTS:-24}"
RETRY_DELAY_SECONDS="${CDN_VERIFY_DELAY_SECONDS:-10}"
CACHE_BUSTER="${GITHUB_RUN_ID:-manual}-${GITHUB_RUN_ATTEMPT:-0}-${VERSION}"
VERSION_RE='^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z][0-9A-Za-z.-]*)?$'

die() { echo "ERROR: $*" >&2; exit 1; }

sha256_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

file_size() {
  stat -c %s "$1" 2>/dev/null || stat -f %z "$1"
}

download_exact() {
  local url="$1" expected_file="$2" label="$3"
  local expected_hash output actual_hash attempt request_url
  expected_hash="$(sha256_file "$expected_file")"
  output="$WORK_DIR/download"
  for attempt in $(seq 1 "$MAX_ATTEMPTS"); do
    request_url="${url}?ci_verify=${CACHE_BUSTER}-${attempt}"
    if curl --compressed --retry 3 --retry-all-errors --connect-timeout 15 --max-time 180 \
      -H 'Cache-Control: no-cache' -fsSL "$request_url" -o "$output"; then
      actual_hash="$(sha256_file "$output")"
      if [[ "$actual_hash" == "$expected_hash" ]]; then
        echo "CDN verified: $label ($actual_hash)"
        return 0
      fi
      echo "CDN content mismatch for $label on attempt $attempt" >&2
    else
      echo "CDN download failed for $label on attempt $attempt" >&2
    fi
    if [[ "$attempt" -lt "$MAX_ATTEMPTS" ]]; then sleep "$RETRY_DELAY_SECONDS"; fi
  done
  die "CDN did not serve expected content: $url"
}

[[ "$VERSION" =~ $VERSION_RE ]] || die "invalid version: $VERSION"
[[ -d "$DIST_DIR" ]] || die "dist directory is missing: $DIST_DIR"
[[ -d "$INDEX_DIR" ]] || die "index directory is missing: $INDEX_DIR"
MANIFEST="$DIST_DIR/manifest.json"
[[ -s "$MANIFEST" ]] || die "manifest is missing: $MANIFEST"
jq -e --arg version "$VERSION" '.version == $version and (.assets | length == 4)' \
  "$MANIFEST" >/dev/null || die "manifest version or asset count is invalid"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

download_exact "$PUBLIC_BASE_URL/$VERSION/manifest.json" "$MANIFEST" "version manifest"
download_exact "$PUBLIC_BASE_URL/$VERSION/SHA256SUMS" "$DIST_DIR/SHA256SUMS" "SHA256SUMS"

while IFS=$'\t' read -r filename expected_hash expected_size; do
  local_file="$DIST_DIR/$filename"
  checksum_file="$local_file.sha256.txt"
  [[ -s "$local_file" ]] || die "local release artifact is missing: $local_file"
  [[ -s "$checksum_file" ]] || die "local checksum is missing: $checksum_file"
  [[ "$(sha256_file "$local_file")" == "$expected_hash" ]] || die "local artifact hash mismatch: $filename"
  [[ "$(file_size "$local_file")" == "$expected_size" ]] || die "local artifact size mismatch: $filename"
  [[ "$(cut -d' ' -f1 "$checksum_file")" == "$expected_hash" ]] || die "checksum sidecar mismatch: $filename"
  download_exact "$PUBLIC_BASE_URL/$VERSION/$filename" "$local_file" "$filename"
  download_exact "$PUBLIC_BASE_URL/$VERSION/$filename.sha256.txt" "$checksum_file" "$filename.sha256.txt"
done < <(jq -r '.assets[] | [.filename, .sha256, (.size | tostring)] | @tsv' "$MANIFEST")

download_exact "$PUBLIC_BASE_URL/latest.json" "$INDEX_DIR/latest.json" "latest.json"
download_exact "$PUBLIC_BASE_URL/latest.txt" "$INDEX_DIR/latest.txt" "latest.txt"
download_exact "$PUBLIC_BASE_URL/latest-version.txt" "$INDEX_DIR/latest-version.txt" "latest-version.txt"
download_exact "$PUBLIC_BASE_URL/releases.json" "$INDEX_DIR/releases.json" "releases.json"

echo "Aliyun CDN release verification passed for YTray $VERSION"
