#!/usr/bin/env bash
# Build the Bazaar Android client as an AAB (Play closed test) and an APK (sideload).
#
# Signed when a keystore is supplied through environment variables, unsigned otherwise.
# Keystores and passwords never belong in the repo; they are read from the CI secret store.
#
# Environment:
#   ANDROID_PROJECT            Path to the Android head .csproj
#                              (default: src/Bazaar.Client.Android/Bazaar.Client.Android.csproj)
#   ANDROID_TFM                Target framework (default: net11.0-android, matching global.json and CLAUDE.md)
#   ANDROID_CONFIGURATION      Build configuration (default: Release)
#   ANDROID_OUT                Output directory (default: artifacts/android)
#   ANDROID_VERSION_CODE       Play versionCode, an integer that must rise with every upload.
#                              Default: the commit count of HEAD (needs full history; CI uses fetch-depth 0).
#   ANDROID_KEYSTORE_PATH      Optional. Path to the upload keystore. Setting it turns on signing.
#   ANDROID_KEY_ALIAS          Required with ANDROID_KEYSTORE_PATH
#   ANDROID_KEYSTORE_PASSWORD  Required with ANDROID_KEYSTORE_PATH (read by MSBuild via env:)
#   ANDROID_KEY_PASSWORD       Required with ANDROID_KEYSTORE_PATH (read by MSBuild via env:)
#   DOTNET                     Optional. dotnet executable to run (default: dotnet on PATH). Used by the tests.
#
# Exit codes: 0 success, 1 build or config error, 2 Android project missing.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="${ANDROID_PROJECT:-$ROOT/src/Bazaar.Client.Android/Bazaar.Client.Android.csproj}"
TFM="${ANDROID_TFM:-net11.0-android}"
CONFIG="${ANDROID_CONFIGURATION:-Release}"
OUT="${ANDROID_OUT:-$ROOT/artifacts/android}"
DOTNET_EXE="${DOTNET:-dotnet}"

die() { echo "error: $*" >&2; exit 1; }

if [[ ! -f "$PROJECT" ]]; then
  echo "error: Android client project not found: $PROJECT" >&2
  echo "Create the Bazaar.Client.Android head (doc 29), or set ANDROID_PROJECT to its .csproj." >&2
  echo "Nothing was built." >&2
  exit 2
fi

if [[ -d "$HOME/.dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

command -v "$DOTNET_EXE" >/dev/null 2>&1 || die "dotnet SDK not found on PATH"

# Play versionCode: integers only, at most 2100000000. Commit count rises on main, so each upload from main is higher.
if [[ -n "${ANDROID_VERSION_CODE:-}" ]]; then
  VERSION_CODE="$ANDROID_VERSION_CODE"
else
  VERSION_CODE="$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || true)"
fi
[[ "$VERSION_CODE" =~ ^[1-9][0-9]{0,8}$ ]] \
  || die "versionCode must be a positive integer, got '${VERSION_CODE:-<empty>}' (set ANDROID_VERSION_CODE or fetch full history)"

# Signing is all or nothing. A keystore path with missing passwords is an error,
# never a silent fall-back to an unsigned build.
SIGN_ARGS=()
if [[ -n "${ANDROID_KEYSTORE_PATH:-}" ]]; then
  [[ -f "$ANDROID_KEYSTORE_PATH" ]] || die "ANDROID_KEYSTORE_PATH does not exist: $ANDROID_KEYSTORE_PATH"
  [[ -n "${ANDROID_KEY_ALIAS:-}" ]] || die "ANDROID_KEY_ALIAS is required when signing"
  [[ -n "${ANDROID_KEYSTORE_PASSWORD:-}" ]] || die "ANDROID_KEYSTORE_PASSWORD is required when signing"
  [[ -n "${ANDROID_KEY_PASSWORD:-}" ]] || die "ANDROID_KEY_PASSWORD is required when signing"
  SIGN_ARGS=(
    "-p:AndroidKeyStore=true"
    "-p:AndroidSigningKeyStore=$ANDROID_KEYSTORE_PATH"
    "-p:AndroidSigningKeyAlias=$ANDROID_KEY_ALIAS"
    "-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD"
    "-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD"
  )
  MODE="signed"
else
  echo "warning: no ANDROID_KEYSTORE_PATH set; building UNSIGNED artifacts (not uploadable to Play)." >&2
  MODE="unsigned"
fi

echo "Building $PROJECT ($CONFIG, $TFM, $MODE, versionCode $VERSION_CODE)"
# BazaarAndroid=true switches the head from its plain net11.0 stub to the Android TFM (see the csproj).
"$DOTNET_EXE" publish "$PROJECT" -c "$CONFIG" -f "$TFM" \
  "-p:BazaarAndroid=true" \
  "-p:ApplicationVersion=$VERSION_CODE" \
  "-p:AndroidPackageFormats=aab%3Bapk" \
  "${SIGN_ARGS[@]}" \
  || die "dotnet publish failed"

PROJECT_DIR="$(dirname "$PROJECT")"
mkdir -p "$OUT"
# Drop artifacts from an earlier run so a stale build is never copied next to a new one.
find "$OUT" -maxdepth 1 -type f \( -name 'signed-*' -o -name 'unsigned-*' -o -name 'SHA256SUMS.txt' \) -delete
COPIED=0
while IFS= read -r -d '' FILE; do
  NAME="$(basename "$FILE")"
  cp "$FILE" "$OUT/$MODE-$NAME"
  COPIED=$((COPIED + 1))
done < <(find "$PROJECT_DIR/bin" -type f \( -name '*.apk' -o -name '*.aab' \) -print0 2>/dev/null)

[[ "$COPIED" -gt 0 ]] || die "no .apk or .aab found under $PROJECT_DIR/bin"

(
  cd "$OUT"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -- "$MODE"-*.apk "$MODE"-*.aab > SHA256SUMS.txt 2>/dev/null || true
  fi
)

echo "Wrote $COPIED artifact(s) to $OUT ($MODE, versionCode $VERSION_CODE)"
