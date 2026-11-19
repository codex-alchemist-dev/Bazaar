#!/usr/bin/env bash
# Builds the Bazaar Linux client Flatpak bundle and its SHA-256 file (doc 30 section 3, doc 37).
#
# Usage: packaging/linux-flatpak/build-flatpak.sh [output-dir]
#   output-dir defaults to <repo>/out/flatpak (or $BAZAAR_FLATPAK_OUT).
#
# This script never downloads anything itself: the operator supplies flatpak, flatpak-builder and the
# runtime, SDK and SDK extension named in the manifest. Missing pieces stop the script with a clear error.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
manifest="$here/TheBazaarAuthors.BazaarClient.yml"
app_id="TheBazaarAuthors.BazaarClient"
out="${1:-${BAZAAR_FLATPAK_OUT:-$repo/out/flatpak}}"
work="${BAZAAR_FLATPAK_WORK:-$repo/obj/flatpak}"

die() { echo "build-flatpak: $*" >&2; exit 1; }

command -v flatpak >/dev/null 2>&1 || die "flatpak is not installed. Install it with your distro's package manager (this script does not install tools)."
command -v flatpak-builder >/dev/null 2>&1 || die "flatpak-builder is not installed. Install it with your distro's package manager (this script does not install tools)."
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is not installed (coreutils)."
[ -f "$manifest" ] || die "manifest not found: $manifest"

# Read the pinned runtime branch and SDK extensions from the manifest (top-level scalar and list).
branch="$(sed -n "s/^runtime-version:[[:space:]]*'\{0,1\}\([^'[:space:]#]*\)'\{0,1\}.*/\1/p" "$manifest" | head -n1)"
[ -n "$branch" ] || die "could not read runtime-version from the manifest"
extensions="$(sed -n "s/^[[:space:]]*-[[:space:]]*\(org\.freedesktop\.Sdk\.Extension\.[A-Za-z0-9_.-]*\).*/\1/p" "$manifest")"

# The operator must already have the runtime, SDK and extensions installed (user or system install).
need=("org.freedesktop.Platform//$branch" "org.freedesktop.Sdk//$branch")
for ext in $extensions; do need+=("$ext//$branch"); done
missing=()
for ref in "${need[@]}"; do
  flatpak info "$ref" >/dev/null 2>&1 || missing+=("$ref")
done
if [ "${#missing[@]}" -gt 0 ]; then
  {
    echo "build-flatpak: missing Flatpak runtime pieces (this script never downloads them):"
    printf '  %s\n' "${missing[@]}"
    echo "Install them from your configured remote, for example:"
    echo "  flatpak install --user flathub ${missing[*]}"
  } >&2
  exit 1
fi

mkdir -p "$out" "$work"
rm -rf "$work/repo" "$work/build-dir"

# Run from the manifest directory so the manifest's relative `path: ../..` source resolves to the repo root.
cd "$here"
flatpak-builder --user --force-clean --disable-cache \
  --repo="$work/repo" --state-dir="$work/state" \
  "$work/build-dir" "$manifest"

bundle="$out/$app_id.flatpak"
rm -f "$bundle" "$bundle.sha256"
flatpak build-bundle "$work/repo" "$bundle" "$app_id"

# The checksum file uses the bare file name so `sha256sum -c` works from the output directory.
( cd "$out" && sha256sum "$app_id.flatpak" > "$app_id.flatpak.sha256" )

echo "bundle: $bundle"
echo "sha256: $(cut -d' ' -f1 "$bundle.sha256")"
