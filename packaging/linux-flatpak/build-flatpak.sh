#!/usr/bin/env bash
# Builds the Bazaar Linux client Flatpak bundle and its SHA-256 file (doc 30 section 3, doc 37).
#
# Usage: packaging/linux-flatpak/build-flatpak.sh [--arch x86_64|aarch64] [--render-only] [output-dir]
#   output-dir defaults to <repo>/out/flatpak (or $BAZAAR_FLATPAK_OUT). --arch defaults to this machine.
#   --render-only stops after rendering the manifest into the scratch dir (no flatpak tools needed).
#
# Flatpak compiles nothing: the manifest installs the reproducible self-contained tarball made by
# packaging/linux/build-tarball.sh (set BAZAAR_FLATPAK_TARBALL to reuse an existing one). The rendered manifest
# and the staged inputs go to a scratch dir ($BAZAAR_FLATPAK_WORK, default <repo>/obj/flatpak); the checked-in
# manifest is never edited.
#
# This script never downloads anything itself: the operator supplies flatpak, flatpak-builder and the
# org.freedesktop.Platform and org.freedesktop.Sdk runtimes. Missing pieces stop the script with a clear error.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
manifest="$here/io.github.codex_alchemist_dev.BazaarClient.yml"
app_id="io.github.codex_alchemist_dev.BazaarClient"
work="${BAZAAR_FLATPAK_WORK:-$repo/obj/flatpak}"

die() { echo "build-flatpak: $*" >&2; exit 1; }

arch="$(uname -m)"
render_only=0
out="${BAZAAR_FLATPAK_OUT:-$repo/out/flatpak}"
while [ $# -gt 0 ]; do
  case "$1" in
    --arch) [ $# -ge 2 ] || die "--arch needs a value"; arch="$2"; shift 2 ;;
    --render-only) render_only=1; shift ;;
    -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}"; exit 0 ;;
    -*) die "unknown option: $1" ;;
    *) out="$1"; shift ;;
  esac
done
case "$arch" in
  x86_64|aarch64) ;;
  *) die "unsupported arch: $arch (use x86_64 or aarch64)" ;;
esac

command -v sha256sum >/dev/null 2>&1 || die "sha256sum is not installed (coreutils)."
[ -f "$manifest" ] || die "manifest not found: $manifest"

# Read the pinned runtime branch from the manifest.
branch="$(sed -n "s/^runtime-version:[[:space:]]*'\{0,1\}\([^'[:space:]#]*\)'\{0,1\}.*/\1/p" "$manifest" | head -n1)"
[ -n "$branch" ] || die "could not read runtime-version from the manifest"

if [ "$render_only" -eq 0 ]; then
  command -v flatpak >/dev/null 2>&1 || die "flatpak is not installed. Install it with your distro's package manager (this script does not install tools)."
  command -v flatpak-builder >/dev/null 2>&1 || die "flatpak-builder is not installed. Install it with your distro's package manager (this script does not install tools)."
  # The operator must already have the runtime and SDK installed (user or system install).
  missing=()
  for ref in "org.freedesktop.Platform//$branch" "org.freedesktop.Sdk//$branch"; do
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
fi

# 1. The tarball: reuse the supplied one, else build it with the baseline packaging script.
tarball="${BAZAAR_FLATPAK_TARBALL:-}"
if [ -z "$tarball" ]; then
  tar_out="$work/tarball"
  rm -rf "$tar_out"
  OUT_DIR="$tar_out" "$repo/packaging/linux/build-tarball.sh" --arch "$arch"
  shopt -s nullglob
  built=("$tar_out"/Bazaar-*-linux-"$arch".tar.gz)
  shopt -u nullglob
  [ "${#built[@]}" -eq 1 ] || die "expected one tarball in $tar_out, found ${#built[@]}"
  tarball="${built[0]}"
fi
[ -f "$tarball" ] || die "tarball not found: $tarball"
sha="$(sha256sum "$tarball" | cut -d' ' -f1)"

# 2. Stage the inputs and render the manifest into the scratch dir (the checked-in manifest stays a template).
stage="$work/stage"
rm -rf "$stage"
mkdir -p "$stage"
cp "$tarball" "$stage/bazaar-client.tar.gz"
cp "$here/$app_id.desktop" "$here/$app_id.metainfo.xml" "$stage/"
cp "$repo/packaging/linux-appimage/bazaar-client.svg" "$stage/"
sed -e "s|@TARBALL@|bazaar-client.tar.gz|g" -e "s|@SHA256@|$sha|g" "$manifest" > "$stage/$app_id.yml"
if grep -q '@[A-Z0-9_]*@' "$stage/$app_id.yml"; then
  die "unreplaced placeholder left in the rendered manifest"
fi
echo "manifest: $stage/$app_id.yml"
echo "tarball sha256: $sha"
[ "$render_only" -eq 0 ] || exit 0

# 3. Build the bundle (nothing in the rendered manifest needs the network).
mkdir -p "$out"
rm -rf "$work/repo" "$work/build-dir"
cd "$stage"
flatpak-builder --user --force-clean --disable-cache --arch="$arch" \
  --repo="$work/repo" --state-dir="$work/state" \
  "$work/build-dir" "$stage/$app_id.yml"

bundle="$out/$app_id.flatpak"
rm -f "$bundle" "$bundle.sha256"
flatpak build-bundle --arch="$arch" "$work/repo" "$bundle" "$app_id"

# The checksum file uses the bare file name so `sha256sum -c` works from the output directory.
( cd "$out" && sha256sum "$app_id.flatpak" > "$app_id.flatpak.sha256" )

echo "bundle: $bundle"
echo "sha256: $(cut -d' ' -f1 "$bundle.sha256")"
