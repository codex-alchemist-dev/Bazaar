#!/usr/bin/env bash
# Wraps an UNSIGNED .pkg in a compressed .dmg disk image using hdiutil.
#
# The .pkg sits at the top level of the image, which is what the Homebrew cask
# in Casks/bazaar-client.rb expects (its `pkg` stanza is relative to the mount).
#
# Usage:
#   build-dmg.sh --pkg PATH --version 0.MM.PP --output-dir DIR
#
# Not signed and not notarised (doc 37, section 7).
set -euo pipefail

pkg=""
version=""
output_dir=""

usage() {
  sed -n '2,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --pkg)        pkg="$2"; shift 2 ;;
    --version)    version="$2"; shift 2 ;;
    --output-dir) output_dir="$2"; shift 2 ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ -n "$pkg" && -n "$version" && -n "$output_dir" ]] || { usage >&2; exit 2; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must be numeric 0.MM.PP, got: $version" >&2; exit 2; }
[[ -f "$pkg" ]] || { echo "package not found: $pkg" >&2; exit 2; }
command -v hdiutil >/dev/null || { echo "hdiutil not found (run on macOS)" >&2; exit 1; }

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

stage="$work_dir/stage"
mkdir -p "$stage" "$output_dir"
cp "$pkg" "$stage/Bazaar-Client-$version.pkg"

dmg="$output_dir/Bazaar-Client-$version.dmg"
rm -f "$dmg"
hdiutil create \
  -volname "Bazaar Client $version" \
  -srcfolder "$stage" \
  -fs HFS+ \
  -format UDZO \
  -imagekey zlib-level=9 \
  -ov \
  "$dmg"

shasum -a 256 "$dmg" | awk '{print $1}' > "$dmg.sha256"
echo "Wrote unsigned disk image: $dmg"
echo "SHA-256 written to: $dmg.sha256"
