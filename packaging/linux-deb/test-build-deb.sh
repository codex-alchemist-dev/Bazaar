#!/usr/bin/env bash
# Tests packaging/linux-deb/build-deb.sh against a fake publish directory (no dotnet needed).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BUILD="${SCRIPT_DIR}/build-deb.sh"

if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "SKIP: dpkg-deb is not installed; the deb test needs dpkg-dev."
    exit 0
fi

fail() { echo "FAIL: $*" >&2; exit 1; }

EXPECTED_VERSION="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n1)"
[[ -n "${EXPECTED_VERSION}" ]] || fail "no VersionPrefix in Directory.Build.props"

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
export SOURCE_DATE_EPOCH=1700000000

FAKE="${TMP}/publish"
mkdir -p "${FAKE}/runtimes"
printf '#!/bin/sh\nexit 0\n' > "${FAKE}/Bazaar.Client.Linux"; chmod 755 "${FAKE}/Bazaar.Client.Linux"
echo dll > "${FAKE}/Bazaar.Client.Core.dll"
echo dll > "${FAKE}/runtimes/native.so"
echo pdb > "${FAKE}/Bazaar.Client.Core.pdb"

for arch in amd64 arm64; do
    bash "${BUILD}" --publish-dir "${FAKE}" --arch "${arch}" --out-dir "${TMP}/a-${arch}" >/dev/null
    bash "${BUILD}" --publish-dir "${FAKE}" --arch "${arch}" --out-dir "${TMP}/b-${arch}" >/dev/null
    deb="${TMP}/a-${arch}/bazaar-client_${EXPECTED_VERSION}_${arch}.deb"
    [[ -f "${deb}" ]] || fail "missing ${deb}"
    [[ -f "${deb}.sha256" ]] || fail "missing .sha256 for ${arch}"

    [[ "$(dpkg-deb --field "${deb}" Package)" == "bazaar-client" ]] || fail "package name (${arch})"
    [[ "$(dpkg-deb --field "${deb}" Version)" == "${EXPECTED_VERSION}" ]] || fail "version (${arch})"
    [[ "$(dpkg-deb --field "${deb}" Architecture)" == "${arch}" ]] || fail "architecture (${arch})"

    contents="$(dpkg-deb --contents "${deb}")"
    grep -q ' \./opt/bazaar-client/Bazaar.Client.Linux$' <<<"${contents}" || fail "payload binary missing (${arch})"
    grep -q ' \./opt/bazaar-client/runtimes/native.so$' <<<"${contents}" || fail "payload subfolder missing (${arch})"
    grep -q ' \./usr/bin/bazaar-client -> /opt/bazaar-client/Bazaar.Client.Linux$' <<<"${contents}" || fail "usr/bin symlink missing (${arch})"
    grep -q ' \./usr/share/applications/bazaar-client.desktop$' <<<"${contents}" || fail "desktop file missing (${arch})"
    grep -q ' \./usr/share/icons/hicolor/scalable/apps/bazaar-client.svg$' <<<"${contents}" || fail "icon missing (${arch})"
    if grep -q '\.pdb$' <<<"${contents}"; then fail "pdb shipped (${arch})"; fi
    if grep -v ' root/root ' <<<"${contents}" | grep -q .; then fail "entry not owned by root:root (${arch})"; fi

    a="$(sha256sum "${deb}" | cut -d' ' -f1)"
    b="$(sha256sum "${TMP}/b-${arch}/$(basename "${deb}")" | cut -d' ' -f1)"
    [[ "${a}" == "${b}" ]] || fail "two builds differ (${arch}): ${a} ${b}"
    [[ "$(cut -d' ' -f1 "${deb}.sha256")" == "${a}" ]] || fail ".sha256 does not match the package (${arch})"
done

# A missing dpkg-deb is a clear error, not a crash.
EMPTY="${TMP}/empty"; mkdir "${EMPTY}"
if out="$(PATH="${EMPTY}" "${BASH}" "${BUILD}" --publish-dir "${FAKE}" 2>&1)"; then fail "expected failure without dpkg-deb"; fi
grep -q 'dpkg-deb is not installed' <<<"${out}" || fail "missing-tool message not clear: ${out}"

echo "PASS: build-deb.sh (amd64 and arm64, reproducible, layout, fields)"
