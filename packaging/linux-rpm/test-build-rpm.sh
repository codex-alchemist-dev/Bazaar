#!/usr/bin/env bash
# Tests packaging/linux-rpm/build-rpm.sh against a fake publish directory (no dotnet needed). Runs only where rpmbuild
# exists; otherwise it prints a SKIP line naming the missing tool and exits 0.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
BUILD="${SCRIPT_DIR}/build-rpm.sh"

for tool in rpmbuild rpm; do
    if ! command -v "${tool}" >/dev/null 2>&1; then
        echo "SKIP: ${tool} is not installed; the rpm test needs rpm-build and rpm."
        exit 0
    fi
done

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

for arch in x86_64 aarch64; do
    bash "${BUILD}" --publish-dir "${FAKE}" --arch "${arch}" --out-dir "${TMP}/a-${arch}" >/dev/null
    bash "${BUILD}" --publish-dir "${FAKE}" --arch "${arch}" --out-dir "${TMP}/b-${arch}" >/dev/null
    rpmfile="${TMP}/a-${arch}/bazaar-client-${EXPECTED_VERSION}-1.${arch}.rpm"
    [[ -f "${rpmfile}" ]] || fail "missing ${rpmfile}"
    [[ -f "${rpmfile}.sha256" ]] || fail "missing .sha256 for ${arch}"

    [[ "$(rpm -qp --qf '%{NAME}' "${rpmfile}")" == "bazaar-client" ]] || fail "package name (${arch})"
    [[ "$(rpm -qp --qf '%{VERSION}' "${rpmfile}")" == "${EXPECTED_VERSION}" ]] || fail "version (${arch})"
    [[ "$(rpm -qp --qf '%{ARCH}' "${rpmfile}")" == "${arch}" ]] || fail "architecture (${arch})"

    files="$(rpm -qlp "${rpmfile}")"
    grep -qx '/opt/bazaar-client/Bazaar.Client.Linux' <<<"${files}" || fail "payload binary missing (${arch})"
    grep -qx '/opt/bazaar-client/runtimes/native.so' <<<"${files}" || fail "payload subfolder missing (${arch})"
    grep -qx '/usr/bin/bazaar-client' <<<"${files}" || fail "usr/bin symlink missing (${arch})"
    grep -qx '/usr/share/applications/bazaar-client.desktop' <<<"${files}" || fail "desktop file missing (${arch})"
    grep -qx '/usr/share/icons/hicolor/scalable/apps/bazaar-client.svg' <<<"${files}" || fail "icon missing (${arch})"
    if grep -q '\.pdb$' <<<"${files}"; then fail "pdb shipped (${arch})"; fi
    if rpm -qlp --qf '[%{FILEUSERNAME}:%{FILEGROUPNAME}\n]' "${rpmfile}" | grep -v '^root:root$' | grep -q .; then
        fail "entry not owned by root:root (${arch})"
    fi
    [[ "$(rpm -qp --qf '[%{FILELINKTOS}\n]' "${rpmfile}" | grep -c .)" == "1" ]] || fail "expected exactly one symlink (${arch})"

    a="$(sha256sum "${rpmfile}" | cut -d' ' -f1)"
    b="$(sha256sum "${TMP}/b-${arch}/$(basename "${rpmfile}")" | cut -d' ' -f1)"
    [[ "${a}" == "${b}" ]] || fail "two builds differ (${arch}): ${a} ${b}"
    [[ "$(cut -d' ' -f1 "${rpmfile}.sha256")" == "${a}" ]] || fail ".sha256 does not match the package (${arch})"
done

# A missing rpmbuild is a clear error, not a crash.
EMPTY="${TMP}/empty"; mkdir "${EMPTY}"
if out="$(PATH="${EMPTY}" "${BASH}" "${BUILD}" --publish-dir "${FAKE}" 2>&1)"; then fail "expected failure without rpmbuild"; fi
grep -q 'rpmbuild is not installed' <<<"${out}" || fail "missing-tool message not clear: ${out}"

echo "PASS: build-rpm.sh (x86_64 and aarch64, reproducible, layout, fields)"
