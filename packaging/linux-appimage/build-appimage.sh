#!/usr/bin/env bash
# Builds a self-contained Bazaar Linux client and packages it as an AppImage.
#
# Usage:
#   packaging/linux-appimage/build-appimage.sh [--arch x86_64|aarch64] [--version X.Y.Z]
#
# Environment:
#   APPIMAGETOOL  path to an appimagetool binary (default: appimagetool on PATH).
#                 This script never downloads appimagetool; supply it yourself.
#   OUT_DIR       output directory (default: artifacts/linux-appimage under repo root).
#
# Steps: dotnet publish (self-contained) -> AppDir -> appimagetool.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj"
APP_BINARY="Bazaar.Client.Linux"
APP_ID="bazaar-client"

ARCH="x86_64"
VERSION=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) ARCH="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        -h|--help)
            sed -n '2,11p' "${BASH_SOURCE[0]}"; exit 0 ;;
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

echo "==> Running appimagetool"
mkdir -p "${OUT_DIR}"
ARCH="${ARCH}" "${APPIMAGETOOL}" "${APPDIR}" "${OUTPUT}"

echo "==> Wrote ${OUTPUT}"
