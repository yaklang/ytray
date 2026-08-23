#!/usr/bin/env bash
set -euo pipefail

APP_PATH="${1:?usage: macos-codesign.sh <app>}"
[[ -d "$APP_PATH" ]] || { echo "App bundle not found: $APP_PATH" >&2; exit 1; }
: "${APPLE_CERTIFICATE_BASE64:?APPLE_CERTIFICATE_BASE64 is required}"
: "${APPLE_CERTIFICATE_PASSWORD:?APPLE_CERTIFICATE_PASSWORD is required}"

KEYCHAIN_PASSWORD="${APPLE_KEYCHAIN_PASSWORD:-ytray-ci-signing}"
KEYCHAIN="${RUNNER_TEMP:-/tmp}/ytray-signing-$$.keychain-db"
CERTIFICATE="$(mktemp -t ytray-certificate).p12"
ORIGINAL_KEYCHAINS=()
while IFS= read -r keychain; do
    keychain="${keychain#${keychain%%[![:space:]]*}}"
    keychain="${keychain#\"}"
    keychain="${keychain%\"}"
    [[ -n "$keychain" ]] && ORIGINAL_KEYCHAINS+=("$keychain")
done < <(security list-keychains -d user)

cleanup() {
    security list-keychains -d user -s "${ORIGINAL_KEYCHAINS[@]}" 2>/dev/null || true
    security delete-keychain "$KEYCHAIN" 2>/dev/null || true
    rm -f "$CERTIFICATE"
}
trap cleanup EXIT

printf '%s' "$APPLE_CERTIFICATE_BASE64" | base64 --decode > "$CERTIFICATE"
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 3600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security list-keychains -d user -s "$KEYCHAIN" "${ORIGINAL_KEYCHAINS[@]}"
security import "$CERTIFICATE" -k "$KEYCHAIN" -P "$APPLE_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null

IDENTITY="${APPLE_SIGN_IDENTITY:-}"
if [[ -z "$IDENTITY" && -n "${APPLE_TEAM_ID:-}" ]]; then
    IDENTITY="$(security find-identity -v -p codesigning "$KEYCHAIN" | awk -v team="$APPLE_TEAM_ID" 'index($0, team) { split($0,a,"\""); print a[2]; exit }')"
fi
if [[ -z "$IDENTITY" ]]; then
    IDENTITY="$(security find-identity -v -p codesigning "$KEYCHAIN" | awk 'NR == 1 { split($0,a,"\""); print a[2] }')"
fi
[[ -n "$IDENTITY" ]] || { echo "No Developer ID signing identity found" >&2; exit 1; }

while IFS= read -r nested; do
    codesign --force --options runtime --timestamp --sign "$IDENTITY" "$nested"
done < <(find "$APP_PATH" \( -name '*.dylib' -o -name '*.framework' \) -print)
codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP_PATH"
codesign --verify --deep --strict --verbose=2 "$APP_PATH"
