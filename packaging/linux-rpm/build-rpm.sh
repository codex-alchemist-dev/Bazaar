#!/usr/bin/env bash
# Builds the Bazaar Linux client .rpm from a self-contained publish output (doc 30, section 3; doc 37: deb and rpm are
# built by the company server). The operator supplies rpmbuild; this script never downloads or installs anything.
#
# Usage:
#   packaging/linux-rpm/build-rpm.sh [--publish-dir DIR] [--arch x86_64|aarch64] [--version X.Y.Z] [--out-dir DIR]
#
# Without --publish-dir the script runs `dotnet publish` (self-contained, linux-x64 or linux-arm64) itself.
#
# Environment:
#   SOURCE_DATE_EPOCH timestamp stamped on every file and the build time (default: the HEAD commit time).
#
# Layout matches the deb: payload in /opt/bazaar-client, /usr/bin/bazaar-client symlink, desktop entry and scalable icon.
# Output: bazaar-client-<version>-1.<arch>.rpm and a .sha256 file beside it. Signing is a separate step.
set -euo pipefail

# The tool check comes first and uses only builtins, so a missing rpmbuild is reported before anything else runs.
if ! command -v rpmbuild >/dev/null 2>&1; then
    echo "rpmbuild is not installed. Install rpm-build (rpm on Debian and Ubuntu) with your distro's package manager and run again." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj"
SPEC="${SCRIPT_DIR}/bazaar-client.spec"
APP_BINARY="Bazaar.Client.Linux"
APP_ID="bazaar-client"
INSTALL_DIR="/opt/${APP_ID}"

ARCH="x86_64"
VERSION=""
PUBLISH_DIR=""
OUT_DIR="${OUT_DIR:-${REPO_ROOT}/artifacts/linux-rpm}"

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
    x86_64|amd64) ARCH="x86_64"; RID="linux-x64" ;;
    aarch64|arm64) ARCH="aarch64"; RID="linux-arm64" ;;
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

STAGE="${WORK_DIR}/payload"
mkdir -p "${STAGE}${INSTALL_DIR}" "${STAGE}/usr/bin" "${STAGE}/usr/share/applications" \
    "${STAGE}/usr/share/icons/hicolor/scalable/apps"

# Debug symbols are not shipped.
cp -a "${PUBLISH_DIR}/." "${STAGE}${INSTALL_DIR}/"
find "${STAGE}${INSTALL_DIR}" -name '*.pdb' -type f -delete
ln -s "${INSTALL_DIR}/${APP_BINARY}" "${STAGE}/usr/bin/${APP_ID}"

sed -e "s|^Exec=.*|Exec=${APP_ID}|" "${SCRIPT_DIR}/../linux/bazaar.desktop" > "${STAGE}/usr/share/applications/${APP_ID}.desktop"
echo "Icon=${APP_ID}" >> "${STAGE}/usr/share/applications/${APP_ID}.desktop"
cp "${SCRIPT_DIR}/../linux-appimage/${APP_ID}.svg" "${STAGE}/usr/share/icons/hicolor/scalable/apps/${APP_ID}.svg"

# Fixed modes and times; the spec's %defattr makes every entry root:root.
find "${STAGE}" -type d -exec chmod 755 {} +
find "${STAGE}" -type f -perm /111 -exec chmod 755 {} +
find "${STAGE}" -type f ! -perm /111 -exec chmod 644 {} +
find "${STAGE}" -exec touch -h -d "@${SOURCE_DATE_EPOCH}" {} +

TOPDIR="${WORK_DIR}/rpmbuild"
mkdir -p "${TOPDIR}"/{BUILD,RPMS,SRPMS,SPECS,SOURCES,BUILDROOT}

echo "==> Building ${APP_ID} ${VERSION} (${ARCH})"
rpmbuild -bb "${SPEC}" \
    --target "${ARCH}" \
    --define "_topdir ${TOPDIR}" \
    --define "pkg_version ${VERSION}" \
    --define "payload_root ${STAGE}" \
    --define "dist %{nil}" \
    --define "_buildhost reproducible" \
    --define "source_date_epoch_from_changelog 0" \
    --define "use_source_date_epoch_as_buildtime 1" \
    --define "clamp_mtime_to_source_date_epoch 1" \
    --define "_binary_payload w9.gzdio" \
    --define "_binary_filedigest_algorithm 8"

BUILT="$(find "${TOPDIR}/RPMS" -name '*.rpm' -type f | head -n1)"
if [[ -z "${BUILT}" ]]; then
    echo "rpmbuild produced no package." >&2
    exit 1
fi
RPM="${OUT_DIR}/$(basename "${BUILT}")"
cp "${BUILT}" "${RPM}"

(cd "${OUT_DIR}" && sha256sum "$(basename "${RPM}")" > "$(basename "${RPM}").sha256")
echo "==> $(cat "${RPM}.sha256")"
