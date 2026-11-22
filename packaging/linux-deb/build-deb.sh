#!/usr/bin/env bash
# Builds the Bazaar Linux client .deb from a self-contained publish output (doc 30, section 3; doc 37: deb and rpm are
# built by the company server). The operator supplies dpkg-deb; this script never downloads or installs anything.
#
# Usage:
#   packaging/linux-deb/build-deb.sh [--publish-dir DIR] [--arch amd64|arm64] [--version X.Y.Z] [--out-dir DIR]
#
# Without --publish-dir the script runs `dotnet publish` (self-contained, linux-x64 or linux-arm64) itself.
#
# Environment:
#   SOURCE_DATE_EPOCH timestamp stamped on every file (default: the HEAD commit time), so one commit gives one hash.
#
# Layout: payload in /opt/bazaar-client, /usr/bin/bazaar-client symlink, desktop entry and scalable icon under
# /usr/share. Output: bazaar-client_<version>_<arch>.deb and a .sha256 file beside it. Signing is a separate step.
set -euo pipefail

# The tool check comes first and uses only builtins, so a missing dpkg-deb is reported before anything else runs.
if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "dpkg-deb is not installed. Install dpkg-dev with your distro's package manager and run again." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj"
APP_BINARY="Bazaar.Client.Linux"
APP_ID="bazaar-client"
INSTALL_DIR="/opt/${APP_ID}"

ARCH="amd64"
VERSION=""
PUBLISH_DIR=""
OUT_DIR="${OUT_DIR:-${REPO_ROOT}/artifacts/linux-deb}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --publish-dir) PUBLISH_DIR="$2"; shift 2 ;;
        --arch) ARCH="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        --out-dir) OUT_DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "${ARCH}" in
    amd64|x86_64) ARCH="amd64"; RID="linux-x64" ;;
    arm64|aarch64) ARCH="arm64"; RID="linux-arm64" ;;
    *) echo "Unsupported arch: ${ARCH} (use amd64 or arm64)" >&2; exit 2 ;;
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

mkdir -p "${OUT_DIR}"
OUT_DIR="$(cd "${OUT_DIR}" && pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

if [[ -z "${PUBLISH_DIR}" ]]; then
    PUBLISH_DIR="${WORK_DIR}/publish"
    echo "==> Publishing ${APP_BINARY} (${RID}, version ${VERSION})"
    dotnet publish "${PROJECT}" \
        --configuration Release \
        --runtime "${RID}" \
        --self-contained true \
        -p:Version="${VERSION}" \
        --output "${PUBLISH_DIR}"
fi
if [[ ! -f "${PUBLISH_DIR}/${APP_BINARY}" ]]; then
    echo "${PUBLISH_DIR} does not contain ${APP_BINARY}; pass a self-contained publish directory." >&2
    exit 1
fi

STAGE="${WORK_DIR}/root"
mkdir -p "${STAGE}${INSTALL_DIR}" "${STAGE}/usr/bin" "${STAGE}/usr/share/applications" \
    "${STAGE}/usr/share/icons/hicolor/scalable/apps" "${STAGE}/DEBIAN"

# Debug symbols are not shipped.
cp -a "${PUBLISH_DIR}/." "${STAGE}${INSTALL_DIR}/"
find "${STAGE}${INSTALL_DIR}" -name '*.pdb' -type f -delete
ln -s "${INSTALL_DIR}/${APP_BINARY}" "${STAGE}/usr/bin/${APP_ID}"

# The tarball's desktop entry runs the binary from its own folder; here the symlink is on PATH and the icon is installed.
sed -e "s|^Exec=.*|Exec=${APP_ID}|" "${SCRIPT_DIR}/../linux/bazaar.desktop" > "${STAGE}/usr/share/applications/${APP_ID}.desktop"
echo "Icon=${APP_ID}" >> "${STAGE}/usr/share/applications/${APP_ID}.desktop"
cp "${SCRIPT_DIR}/../linux-appimage/${APP_ID}.svg" "${STAGE}/usr/share/icons/hicolor/scalable/apps/${APP_ID}.svg"

# Fixed modes and times make the archive reproducible; dpkg-deb --root-owner-group sets root:root.
find "${STAGE}" -type d -exec chmod 755 {} +
find "${STAGE}" -type f -perm /111 -exec chmod 755 {} +
find "${STAGE}" -type f ! -perm /111 -exec chmod 644 {} +
INSTALLED_KB="$(du -sk --apparent-size "${STAGE}" | cut -f1)"

cat > "${STAGE}/DEBIAN/control" <<CONTROL
Package: ${APP_ID}
Version: ${VERSION}
Architecture: ${ARCH}
Maintainer: The Bazaar Authors <packaging@bazaar.invalid>
Section: net
Priority: optional
Installed-Size: ${INSTALLED_KB}
Depends: libc6, libgcc-s1, libstdc++6, zlib1g
Recommends: libfontconfig1, libx11-6
Homepage: https://github.com/codex-alchemist-dev/Bazaar
Description: Bazaar desktop client
 Group chats and agents for your Bazaar server. The package bundles a
 self-contained .NET build, so no .NET runtime is needed on the host.
 Installed under ${INSTALL_DIR}, with ${APP_ID} on the PATH.
CONTROL
chmod 644 "${STAGE}/DEBIAN/control"

find "${STAGE}" -exec touch -h -d "@${SOURCE_DATE_EPOCH}" {} +

DEB="${OUT_DIR}/${APP_ID}_${VERSION}_${ARCH}.deb"
rm -f "${DEB}"
echo "==> Creating ${DEB}"
dpkg-deb --root-owner-group -Zgzip -z9 --build "${STAGE}" "${DEB}"

(cd "${OUT_DIR}" && sha256sum "$(basename "${DEB}")" > "$(basename "${DEB}").sha256")
echo "==> $(cat "${DEB}.sha256")"
