#!/bin/sh
# Publishes the two guest tools for the browser pool VM image and writes their manifest (docs/design/59, section 11).
#   usage: publish-pool-tools.sh <output-dir> [linux-x64|linux-arm64]
# The output directory gets bazaar-pool-spawn, bazaar-pointer and pool-tools.json (the format PoolToolBundle reads). Both tools are
# published as native AOT binaries, so no .NET runtime is needed in the VM. Copy the directory to the VM and name it in
# Bazaar:BrowserPool:Image:ToolSourceDirectory.
set -eu

out=${1:?usage: publish-pool-tools.sh <output-dir> [linux-x64|linux-arm64]}
rid=${2:-linux-x64}
case "$rid" in
  linux-x64|linux-arm64) ;;
  *) echo "unsupported runtime identifier: $rid" >&2; exit 2 ;;
esac

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

for project in Bazaar.Guest.PoolSpawn:bazaar-pool-spawn Bazaar.Guest.Pointer:bazaar-pointer; do
  name=${project%%:*}
  tool=${project##*:}
  dotnet publish "$root/src/$name/$name.csproj" -c Release -r "$rid" -p:PublishAot=true -p:SelfContained=true -o "$work/$tool-out" --nologo -v q
  install -m 0755 "$work/$tool-out/$tool" "$out/$tool"
done

{
  printf '{\n  "tools": [\n'
  sep=''
  for tool in bazaar-pool-spawn bazaar-pointer; do
    hash=$(sha256sum "$out/$tool" | cut -d ' ' -f 1)
    size=$(wc -c < "$out/$tool" | tr -d ' ')
    printf '%s    {\n      "name": "%s",\n      "file": "%s",\n      "sha256": "%s",\n      "size": %s\n    }' "$sep" "$tool" "$tool" "$hash" "$size"
    sep=',
'
  done
  printf '\n  ]\n}\n'
} > "$out/pool-tools.json"

echo "wrote $out/pool-tools.json for $rid"
