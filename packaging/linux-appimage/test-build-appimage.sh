#!/usr/bin/env bash
# Tests build-appimage.sh with a fake dotnet and a fake appimagetool, so no SDK, network or real tool is needed.
# Checks argument errors, the AppDir layout, the .sha256 file and that the script never fetches appimagetool.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD="${SCRIPT_DIR}/build-appimage.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
FAILED=0

pass() { echo "PASS $1"; }
fail() { echo "FAIL $1" >&2; FAILED=1; }

# Fake dotnet: "publish ... --output DIR" creates the client binary and a pdb there.
mkdir -p "${TMP}/bin"
cat > "${TMP}/bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
out=""
while [[ $# -gt 0 ]]; do [[ "$1" == "--output" ]] && out="$2"; shift; done
mkdir -p "${out}"
printf '#!/bin/sh\necho fake client\n' > "${out}/Bazaar.Client.Linux"
chmod +x "${out}/Bazaar.Client.Linux"
echo pdb > "${out}/Bazaar.Client.Linux.pdb"
FAKE
# Fake appimagetool: writes the AppDir listing into the output file and records ARCH.
cat > "${TMP}/fake-appimagetool" <<'FAKE'
#!/usr/bin/env bash
echo "ARCH=${ARCH}" > "${TMP_RECORD}"
(cd "$1" && find . | sort) > "$2"
FAKE
chmod +x "${TMP}/bin/dotnet" "${TMP}/fake-appimagetool"
export PATH="${TMP}/bin:${PATH}"
export TMP_RECORD="${TMP}/record"
export SOURCE_DATE_EPOCH=1700000000

expect_exit() { # name expected_code args...
    local name="$1" want="$2"; shift 2
    set +e
    OUT_DIR="${TMP}/out-err" APPIMAGETOOL="${TMP}/fake-appimagetool" "${BUILD}" "$@" >"${TMP}/err.log" 2>&1
    local got=$?
    set -e
    if [[ "${got}" -eq "${want}" ]]; then pass "${name}"; else fail "${name}: exit ${got}, wanted ${want}"; cat "${TMP}/err.log" >&2; fi
}

expect_exit "unknown argument" 2 --bogus
expect_exit "unsupported arch" 2 --arch riscv64
expect_exit "arch without value" 2 --arch

set +e
OUT_DIR="${TMP}/out-err" APPIMAGETOOL="${TMP}/missing-tool" "${BUILD}" --version 1.2.3 >"${TMP}/err.log" 2>&1
code=$?
set -e
if [[ "${code}" -eq 1 ]] && grep -q "appimagetool not found" "${TMP}/err.log"; then pass "missing appimagetool is an error"; else fail "missing appimagetool: exit ${code}"; fi

OUT_DIR="${TMP}/out" APPIMAGETOOL="${TMP}/fake-appimagetool" "${BUILD}" --version 1.2.3 >"${TMP}/build.log" 2>&1 || { fail "build run"; cat "${TMP}/build.log" >&2; }
IMAGE="${TMP}/out/Bazaar-1.2.3-x86_64.AppImage"
APPDIR="${TMP}/out/work-x86_64/Bazaar.Client.Linux.AppDir"

[[ -f "${IMAGE}" ]] && pass "AppImage written" || fail "AppImage missing"
[[ -x "${APPDIR}/AppRun" ]] && pass "AppRun executable" || fail "AppRun missing or not executable"
[[ -x "${APPDIR}/usr/bin/Bazaar.Client.Linux" ]] && pass "client binary in usr/bin" || fail "client binary missing"
[[ ! -e "${APPDIR}/usr/bin/Bazaar.Client.Linux.pdb" ]] && pass "pdb not shipped" || fail "pdb shipped"
for f in bazaar-client.desktop bazaar-client.svg usr/share/applications/bazaar-client.desktop \
         usr/share/icons/hicolor/scalable/apps/bazaar-client.svg; do
    [[ -f "${APPDIR}/${f}" ]] && pass "AppDir has ${f}" || fail "AppDir missing ${f}"
done
grep -q '^ARCH=x86_64$' "${TMP}/record" && pass "ARCH passed to appimagetool" || fail "ARCH not passed"
[[ "$(stat -c %Y "${APPDIR}/AppRun")" -eq "${SOURCE_DATE_EPOCH}" ]] && pass "AppDir mtimes use SOURCE_DATE_EPOCH" || fail "AppDir mtime not normalised"
if (cd "${TMP}/out" && sha256sum --check --strict "$(basename "${IMAGE}").sha256" >/dev/null); then pass ".sha256 matches"; else fail ".sha256 mismatch"; fi

# Two builds of the same input give the same bytes (the fake tool is deterministic).
first="$(sha256sum "${IMAGE}" | cut -d' ' -f1)"
OUT_DIR="${TMP}/out" APPIMAGETOOL="${TMP}/fake-appimagetool" "${BUILD}" --version 1.2.3 >/dev/null 2>&1
[[ "$(sha256sum "${IMAGE}" | cut -d' ' -f1)" == "${first}" ]] && pass "rebuild is byte-identical" || fail "rebuild differs"

# The script must not download anything itself.
if grep -vE '^[0-9]*:?[[:space:]]*#' "${BUILD}" | grep -qE '\b(curl|wget)\b'; then fail "script fetches over the network"; else pass "script never downloads"; fi

exit "${FAILED}"
