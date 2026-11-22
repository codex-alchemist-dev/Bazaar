#!/usr/bin/env bash
# Builds a self-contained Bazaar Linux client and packages it as an AppImage.
#
# Usage:
#   packaging/linux-appimage/build-appimage.sh [--arch x86_64|aarch64] [--version X.Y.Z] [--verify]
#
# Environment:
#   APPIMAGETOOL      path to an appimagetool binary (default: appimagetool on PATH).
#                     This script never downloads appimagetool; supply it yourself.
#   OUT_DIR           output directory (default: artifacts/linux-appimage under repo root).
#   SOURCE_DATE_EPOCH timestamp for the AppDir files and the squashfs (default: the HEAD commit time).
#
# Steps: dotnet publish (self-contained) -> AppDir -> appimagetool -> .sha256 file beside the AppImage.
# --verify extracts the AppImage and starts its AppRun on Xvfb (x86_64 only, needs xvfb-run); the client must still be
# running when the timeout fires (exit 124), as in scripts/ci/linux-client-startup.sh.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj"
APP_BINARY="Bazaar.Client.Linux"
APP_ID="bazaar-client"

ARCH="x86_64"
VERSION=""
VERIFY=0
VERIFY_SECONDS=20
TIMED_OUT=124

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) [[ $# -ge 2 ]] || { echo "--arch needs a value" >&2; exit 2; }; ARCH="$2"; shift 2 ;;
        --version) [[ $# -ge 2 ]] || { echo "--version needs a value" >&2; exit 2; }; VERSION="$2"; shift 2 ;;
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
export SOURCE_DATE_EPOCH

OUT_DIR="${OUT_DIR:-${REPO_ROOT}/artifacts/linux-appimage}"
WORK_DIR="${OUT_DIR}/work-${ARCH}"
PUBLISH_DIR="${WORK_DIR}/publish"
APPDIR="${WORK_DIR}/${APP_BINARY}.AppDir"
OUTPUT="${OUT_DIR}/Bazaar-${VERSION}-${ARCH}.AppImage"

APPIMAGETOOL="${APPIMAGETOOL:-appimagetool}"
if ! command -v "${APPIMAGETOOL}" >/dev/null 2>&1 && [[ ! -x "${APPIMAGETOOL}" ]]; then
    echo "appimagetool not found. Set APPIMAGETOOL to its path." >&2
    exit 1
fi

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

echo "==> Assembling AppDir"
mkdir -p "${APPDIR}/usr/bin" \
         "${APPDIR}/usr/share/applications" \
         "${APPDIR}/usr/share/icons/hicolor/scalable/apps"
cp -a "${PUBLISH_DIR}/." "${APPDIR}/usr/bin/"
cp "${SCRIPT_DIR}/AppRun" "${APPDIR}/AppRun"
chmod +x "${APPDIR}/AppRun" "${APPDIR}/usr/bin/${APP_BINARY}"
cp "${SCRIPT_DIR}/${APP_ID}.desktop" "${APPDIR}/usr/share/applications/${APP_ID}.desktop"
cp "${SCRIPT_DIR}/${APP_ID}.desktop" "${APPDIR}/${APP_ID}.desktop"
cp "${SCRIPT_DIR}/${APP_ID}.svg" "${APPDIR}/usr/share/icons/hicolor/scalable/apps/${APP_ID}.svg"
cp "${SCRIPT_DIR}/${APP_ID}.svg" "${APPDIR}/${APP_ID}.svg"
# Debug symbols are not shipped; fixed mtimes keep the squashfs reproducible.
rm -f "${APPDIR}/usr/bin/"*.pdb
find "${APPDIR}" -exec touch -h -d "@${SOURCE_DATE_EPOCH}" {} +

echo "==> Running appimagetool"
mkdir -p "${OUT_DIR}"
ARCH="${ARCH}" "${APPIMAGETOOL}" "${APPDIR}" "${OUTPUT}"

(cd "${OUT_DIR}" && sha256sum "$(basename "${OUTPUT}")" > "$(basename "${OUTPUT}").sha256")
echo "==> Wrote ${OUTPUT}"
echo "==> $(cat "${OUTPUT}.sha256")"

if [[ "${VERIFY}" -eq 1 ]]; then
    if [[ "${ARCH}" != "x86_64" ]]; then
        echo "--verify runs the client, so it needs a host of the same architecture (x86_64 here)." >&2
        exit 2
    fi
    command -v xvfb-run >/dev/null || { echo "xvfb-run is not installed" >&2; exit 1; }
    echo "==> Verifying: extract the AppImage and start its AppRun on Xvfb"
    VERIFY_DIR="$(mktemp -d)"
    trap 'rm -rf "${VERIFY_DIR}"' EXIT
    # Extracting avoids needing FUSE on the runner.
    (cd "${VERIFY_DIR}" && "${OUTPUT}" --appimage-extract >/dev/null)
    log="${VERIFY_DIR}/start.log"
    set +e
    timeout --kill-after=5 "${VERIFY_SECONDS}" xvfb-run -a -s "-screen 0 1280x800x24" \
        "${VERIFY_DIR}/squashfs-root/AppRun" >"${log}" 2>&1
    code=$?
    set -e
    if [[ "${code}" -ne "${TIMED_OUT}" ]] || grep -q "Unhandled exception" "${log}"; then
        echo "Verification failed: exit ${code}, expected the client still running after ${VERIFY_SECONDS}s" >&2
        cat "${log}" >&2
        exit 1
    fi
    echo "==> Verified: the extracted AppRun stayed up for ${VERIFY_SECONDS}s"
fi
