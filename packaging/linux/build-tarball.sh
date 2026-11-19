#!/usr/bin/env bash
# Builds a self-contained Bazaar Linux client and packages it as a reproducible tarball (doc 30, section 3: the baseline
# format, no sandbox and no update channel of its own).
#
# Usage:
#   packaging/linux/build-tarball.sh [--arch x86_64|aarch64] [--version X.Y.Z] [--verify]
#
# Environment:
#   OUT_DIR           output directory (default: artifacts/linux-tarball under the repo root).
#   SOURCE_DATE_EPOCH timestamp stamped on every archive entry (default: the HEAD commit time), so the same commit gives
#                     the same bytes.
#
# Output: Bazaar-<version>-linux-<arch>.tar.gz and a .sha256 file beside it. Signing is a separate step (doc 30,
# section 7: SHA-256 sums plus a detached signature). --verify extracts the archive and starts the client on Xvfb
# (x86_64 only, needs xvfb-run); the client must still be running when the timeout fires.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj"
APP_BINARY="Bazaar.Client.Linux"
VERIFY_SECONDS=20
TIMED_OUT=124

ARCH="x86_64"
VERSION=""
VERIFY=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) ARCH="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        --verify) VERIFY=1; shift ;;
        -h|--help)
            sed -n '2,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "${ARCH}" in
    x86_64) RID="linux-x64" ;;
    aarch64) RID="linux-arm64" ;;
    *) echo "Unsupported arch: ${ARCH} (use x86_64 or aarch64)" >&2; exit 2 ;;
esac

if [[ -z "${VERSION}" ]]; then
    # VersionPrefix in Directory.Build.props is the single product version.
    VERSION="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "${REPO_ROOT}/Directory.Build.props" | head -n1)"
fi
if [[ -z "${VERSION}" ]]; then
    echo "Could not read VersionPrefix; pass --version." >&2
    exit 1
fi

if [[ -z "${SOURCE_DATE_EPOCH:-}" ]]; then
    SOURCE_DATE_EPOCH="$(git -C "${REPO_ROOT}" log -1 --format=%ct)"
fi

OUT_DIR="${OUT_DIR:-${REPO_ROOT}/artifacts/linux-tarball}"
WORK_DIR="${OUT_DIR}/work-${ARCH}"
PUBLISH_DIR="${WORK_DIR}/Bazaar-${VERSION}-linux-${ARCH}"
ARCHIVE="${OUT_DIR}/Bazaar-${VERSION}-linux-${ARCH}.tar.gz"

echo "==> Publishing ${APP_BINARY} (${RID}, version ${VERSION})"
rm -rf "${WORK_DIR}"
mkdir -p "${PUBLISH_DIR}"
dotnet publish "${PROJECT}" \
    --configuration Release \
    --runtime "${RID}" \
    --self-contained true \
    -p:Version="${VERSION}" \
    --output "${PUBLISH_DIR}"

if [[ ! -f "${PUBLISH_DIR}/${APP_BINARY}" ]]; then
    echo "Publish did not produce ${APP_BINARY}." >&2
    exit 1
fi

# Debug symbols are not shipped; the desktop entry is, so a user can register the client with their desktop.
rm -f "${PUBLISH_DIR}"/*.pdb
cp "${SCRIPT_DIR}/bazaar.desktop" "${PUBLISH_DIR}/bazaar.desktop"

echo "==> Creating ${ARCHIVE}"
# Sorted names, fixed owner and times, and gzip without a name or timestamp make the archive reproducible.
tar --sort=name \
    --owner=0 --group=0 --numeric-owner \
    --mtime="@${SOURCE_DATE_EPOCH}" \
    -C "${WORK_DIR}" -cf - "$(basename "${PUBLISH_DIR}")" \
    | gzip -n -9 > "${ARCHIVE}"

(cd "${OUT_DIR}" && sha256sum "$(basename "${ARCHIVE}")" > "$(basename "${ARCHIVE}").sha256")
echo "==> $(cat "${ARCHIVE}.sha256")"

if [[ "${VERIFY}" -eq 1 ]]; then
    if [[ "${ARCH}" != "x86_64" ]]; then
        echo "--verify runs the client, so it needs a host of the same architecture (x86_64 here)." >&2
        exit 2
    fi
    command -v xvfb-run >/dev/null || { echo "xvfb-run is not installed" >&2; exit 1; }
    echo "==> Verifying: extract and start on Xvfb"
    VERIFY_DIR="$(mktemp -d)"
    trap 'rm -rf "${VERIFY_DIR}"' EXIT
    tar -xzf "${ARCHIVE}" -C "${VERIFY_DIR}"
    log="${VERIFY_DIR}/start.log"
    set +e
    timeout --kill-after=5 "${VERIFY_SECONDS}" xvfb-run -a -s "-screen 0 1280x800x24" \
        "${VERIFY_DIR}/$(basename "${PUBLISH_DIR}")/${APP_BINARY}" >"${log}" 2>&1
    code=$?
    set -e
    if [[ "${code}" -ne "${TIMED_OUT}" ]] || grep -q "Unhandled exception" "${log}"; then
        echo "Verification failed: exit ${code}, expected the client still running after ${VERIFY_SECONDS}s" >&2
        cat "${log}" >&2
        exit 1
    fi
    echo "==> Verified: the extracted client stayed up for ${VERIFY_SECONDS}s"
fi
