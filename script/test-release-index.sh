#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
VERSION="0.1.0"
BASE="https://aliyun-oss.yaklang.com/ytray/$VERSION"

jq -n --arg base "$BASE" '{
  schema_version: 1, product: "ytray", version: "0.1.0", released_at: "2026-08-23T00:00:00Z",
  plugin: {name: "Yakit Browser Agent", version: "1.2.3"},
  assets: [
    {platform:"darwin",architecture:"arm64",kind:"dmg",filename:"a",url:($base+"/a"),sha256:("a"*64),size:101},
    {platform:"darwin",architecture:"amd64",kind:"dmg",filename:"b",url:($base+"/b"),sha256:("b"*64),size:102},
    {platform:"windows",architecture:"amd64",kind:"setup",filename:"c",url:($base+"/c"),sha256:("c"*64),size:103},
    {platform:"windows",architecture:"386",kind:"setup",filename:"d",url:($base+"/d"),sha256:("d"*64),size:104}
  ]
}' > "$WORK/manifest.json"
printf '%s\n' '{"schema_version":1,"product":"ytray","latest":"0.0.9","versions":[]}' > "$WORK/existing.json"

EXISTING_RELEASES_FILE="$WORK/existing.json" \
  bash "$ROOT/script/prepare-release-index.sh" "$VERSION" "$WORK/manifest.json" "$WORK/out"
jq -e '.latest == "0.1.0" and .versions[0].version == "0.1.0"' "$WORK/out/releases.json" >/dev/null
test "$(tr -d '\r\n' < "$WORK/out/latest.txt")" = "$VERSION"
test "$(tr -d '\r\n' < "$WORK/out/latest-version.txt")" = "$VERSION"
cmp "$WORK/out/latest.txt" "$WORK/out/latest-version.txt"
cmp "$WORK/manifest.json" "$WORK/out/latest.json"

cp "$WORK/out/releases.json" "$WORK/retry-existing.json"
EXISTING_RELEASES_FILE="$WORK/retry-existing.json" \
  bash "$ROOT/script/prepare-release-index.sh" "$VERSION" "$WORK/manifest.json" "$WORK/retry-out"
cmp "$WORK/out/releases.json" "$WORK/retry-out/releases.json"
cmp "$WORK/out/latest-version.txt" "$WORK/retry-out/latest-version.txt"
echo "release index test passed"
