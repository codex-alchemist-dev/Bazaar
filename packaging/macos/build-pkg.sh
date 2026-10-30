#!/usr/bin/env bash
# Builds an UNSIGNED macOS installer (.pkg) from a published Bazaar client folder.
#
# Layout installed by the package:
#   /usr/local/lib/bazaar/            the published folder, copied as-is
#   /usr/local/bin/bazaar-client      wrapper that execs the entry binary
#
# Usage:
#   build-pkg.sh --publish-dir DIR --version 0.MM.PP --output-dir DIR \
#                [--entry-binary NAME] [--identifier ID]
#
#   --publish-dir   output of `dotnet publish` (see example below)
#   --version       numeric SemVer, 0.MM.PP (pkgbuild rejects anything else)
#   --output-dir    where Bazaar-Client-<version>.pkg is written
#   --entry-binary  executable name inside --publish-dir (default: Bazaar.Client.Terminal)
#   --identifier    package identifier (default: dev.codex-alchemist.bazaar.client,
#                   placeholder until the owner confirms the reverse-DNS name)
#
# Example publish step (run on a macOS runner; osx-x64 for Intel):
#   dotnet publish src/Bazaar.Client.Terminal -c Release -r osx-arm64 \
#       --self-contained -o out/publish/osx-arm64
#
# Not signed and not notarised. Signing is parked (doc 37, section 7); the
# release notes must say the build is unsigned. Gatekeeper will block it until
# it is signed and notarised.
set -euo pipefail

publish_dir=""
version=""
output_dir=""
entry_binary="Bazaar.Client.Terminal"
identifier="dev.codex-alchemist.bazaar.client"

usage() {
  sed -n '2,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --publish-dir)  publish_dir="$2"; shift 2 ;;
    --version)      version="$2"; shift 2 ;;
    --output-dir)   output_dir="$2"; shift 2 ;;
    --entry-binary) entry_binary="$2"; shift 2 ;;
    --identifier)   identifier="$2"; shift 2 ;;
    -h|--help)      usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ -n "$publish_dir" && -n "$version" && -n "$output_dir" ]] || { usage >&2; exit 2; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must be numeric 0.MM.PP, got: $version" >&2; exit 2; }
[[ -d "$publish_dir" ]] || { echo "publish dir not found: $publish_dir" >&2; exit 2; }
[[ -f "$publish_dir/$entry_binary" ]] || { echo "entry binary not found: $publish_dir/$entry_binary" >&2; exit 2; }

for tool in pkgbuild productbuild; do
  command -v "$tool" >/dev/null || { echo "$tool not found (run on macOS with Xcode command line tools)" >&2; exit 1; }
done

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

payload="$work_dir/root"
install_dir="$payload/usr/local/lib/bazaar"
bin_dir="$payload/usr/local/bin"
mkdir -p "$install_dir" "$bin_dir"

cp -R "$publish_dir/." "$install_dir/"
chmod +x "$install_dir/$entry_binary"

cat > "$bin_dir/bazaar-client" <<EOF
#!/bin/sh
exec /usr/local/lib/bazaar/$entry_binary "\$@"
EOF
chmod 755 "$bin_dir/bazaar-client"

mkdir -p "$output_dir"
component_pkg="$work_dir/bazaar-client-component.pkg"
distribution="$work_dir/distribution.xml"
product_pkg="$output_dir/Bazaar-Client-$version.pkg"

pkgbuild \
  --root "$payload" \
  --identifier "$identifier" \
  --version "$version" \
  --install-location / \
  "$component_pkg"

# productbuild wraps the component in a distribution so the installer shows a product title.
productbuild --synthesize --package "$component_pkg" "$distribution"
productbuild \
  --distribution "$distribution" \
  --package-path "$work_dir" \
  --version "$version" \
  "$product_pkg"

echo "Wrote unsigned package: $product_pkg"
