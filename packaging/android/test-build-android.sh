#!/usr/bin/env bash
# Tests for build-android.sh. They use a fake dotnet that records its arguments and writes a placeholder AAB and APK,
# so they run on any host with bash and no .NET SDK or Android workload.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/build-android.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

FAILS=0
pass() { echo "ok   - $1"; }
fail() { echo "FAIL - $1"; FAILS=$((FAILS + 1)); }

# Fake project and fake dotnet.
PROJ_DIR="$WORK/src/Bazaar.Client.Android"
mkdir -p "$PROJ_DIR" "$WORK/bin"
PROJ="$PROJ_DIR/Bazaar.Client.Android.csproj"
: > "$PROJ"
cat > "$WORK/bin/fake-dotnet" <<'EOF'
#!/usr/bin/env bash
# Records the arguments, then writes the outputs a real publish would leave under bin/.
printf '%s\n' "$@" > "$FAKE_ARGS_FILE"
# MSBuild splits a -p: value at a bare ';' (the real failure: "MSB1006: Property is not valid. Switch: apk").
# Reproduce that so a bare semicolon in a property value fails here, as it does in CI.
for arg in "$@"; do
  if [[ "$arg" == -p:* && "$arg" == *';'* ]]; then
    echo "MSBUILD : error MSB1006: Property is not valid." >&2
    exit 1
  fi
done
if [[ "${FAKE_DOTNET_FAIL:-0}" == "1" ]]; then exit 1; fi
out="$(dirname "$FAKE_PROJECT")/bin/Release/net11.0-android/publish"
mkdir -p "$out"
echo aab > "$out/org.bazaar.client.aab"
echo apk > "$out/org.bazaar.client.apk"
EOF
chmod +x "$WORK/bin/fake-dotnet"

run() {
  # Runs the script with the fake dotnet; prints the exit code, leaves args in $WORK/args.txt.
  env -i PATH="/usr/bin:/bin" HOME="$WORK/home" \
    DOTNET="$WORK/bin/fake-dotnet" FAKE_ARGS_FILE="$WORK/args.txt" FAKE_PROJECT="$PROJ" \
    ANDROID_PROJECT="$PROJ" ANDROID_OUT="$WORK/out" ANDROID_VERSION_CODE="${VC-42}" \
    "$@" bash "$SCRIPT" >"$WORK/stdout.txt" 2>"$WORK/stderr.txt" && echo 0 || echo $?
}

has_arg() { grep -qxF -- "$1" "$WORK/args.txt"; }

# 1. Missing project exits 2 and builds nothing.
rm -f "$WORK/args.txt"
code="$(env -i PATH="/usr/bin:/bin" HOME="$WORK/home" DOTNET="$WORK/bin/fake-dotnet" \
  FAKE_ARGS_FILE="$WORK/args.txt" FAKE_PROJECT="$PROJ" ANDROID_PROJECT="$WORK/missing.csproj" \
  bash "$SCRIPT" >/dev/null 2>&1 && echo 0 || echo $?)"
if [[ "$code" == 2 && ! -f "$WORK/args.txt" ]]; then pass "missing project exits 2 without building"; else fail "missing project exit=$code"; fi

# 2. Unsigned build: Android TFM, BazaarAndroid, versionCode from env, AAB and APK, no signing args.
rm -rf "$WORK/out" "$WORK/args.txt"
code="$(run)"
if [[ "$code" == 0 ]] && has_arg "-p:BazaarAndroid=true" && has_arg "-p:ApplicationVersion=42" \
   && has_arg "-f" && has_arg "net11.0-android" && has_arg "-p:AndroidPackageFormats=aab%3Bapk" \
   && ! grep -q "AndroidKeyStore" "$WORK/args.txt"; then
  pass "unsigned publish passes the Android TFM, BazaarAndroid, versionCode and both formats"
else
  fail "unsigned publish args (exit $code): $(tr '\n' ' ' < "$WORK/args.txt" 2>/dev/null)"
fi
if [[ -f "$WORK/out/unsigned-org.bazaar.client.aab" && -f "$WORK/out/unsigned-org.bazaar.client.apk" \
   && -f "$WORK/out/SHA256SUMS.txt" ]]; then
  pass "unsigned outputs are copied with the unsigned- prefix and checksummed"
else
  fail "unsigned outputs missing in $WORK/out"
fi

# 3. Stale artifacts from an earlier run are removed.
touch "$WORK/out/signed-old.aab"
code="$(run)"
if [[ "$code" == 0 && ! -e "$WORK/out/signed-old.aab" ]]; then pass "stale signed- artifacts are removed"; else fail "stale artifact kept (exit $code)"; fi

# 4. A keystore without passwords is an error, and dotnet is never called.
rm -f "$WORK/args.txt"
: > "$WORK/upload.keystore"
code="$(run ANDROID_KEYSTORE_PATH="$WORK/upload.keystore" ANDROID_KEY_ALIAS=upload)"
if [[ "$code" == 1 && ! -f "$WORK/args.txt" ]]; then pass "keystore without passwords fails before dotnet runs"; else fail "partial signing config exit=$code"; fi

# 5. Full signing: passwords go by env: reference, never as literal values.
rm -f "$WORK/args.txt"
code="$(run ANDROID_KEYSTORE_PATH="$WORK/upload.keystore" ANDROID_KEY_ALIAS=upload \
  ANDROID_KEYSTORE_PASSWORD=s3cret-store ANDROID_KEY_PASSWORD=s3cret-key)"
if [[ "$code" == 0 ]] && has_arg "-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD" \
   && has_arg "-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD" \
   && ! grep -q "s3cret" "$WORK/args.txt" && [[ -f "$WORK/out/signed-org.bazaar.client.aab" ]]; then
  pass "signed publish references passwords by env: and never on the command line"
else
  fail "signed publish (exit $code)"
fi

# 6. versionCode must be a positive integer.
for bad in "0" "abc" "12345678901"; do
  rm -f "$WORK/args.txt"
  code="$(run ANDROID_VERSION_CODE="$bad")"
  if [[ "$code" == 1 && ! -f "$WORK/args.txt" ]]; then pass "versionCode '$bad' is rejected"; else fail "versionCode '$bad' accepted (exit $code)"; fi
done

# 7. A failing publish is reported as a failure.
code="$(run FAKE_DOTNET_FAIL=1 ANDROID_VERSION_CODE=7)"
if [[ "$code" == 1 ]]; then pass "failed publish exits 1"; else fail "failed publish exit=$code"; fi

if [[ "$FAILS" -ne 0 ]]; then echo "$FAILS test(s) failed"; exit 1; fi
echo "all build-android tests passed"
