#!/usr/bin/env bash
# Tests build-flatpak.sh --render-only with a stand-in tarball (no dotnet, flatpak or network needed):
# the manifest is rendered into a scratch dir with the tarball's real sha256, the checked-in template is left
# untouched, and the staged inputs the manifest names are all present.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

mkdir -p "$tmp/Bazaar-0.0.0-linux-x86_64"
echo stand-in > "$tmp/Bazaar-0.0.0-linux-x86_64/Bazaar.Client.Linux"
tar -C "$tmp" -czf "$tmp/Bazaar-0.0.0-linux-x86_64.tar.gz" Bazaar-0.0.0-linux-x86_64
expected="$(sha256sum "$tmp/Bazaar-0.0.0-linux-x86_64.tar.gz" | cut -d' ' -f1)"

before="$(sha256sum "$here/TheBazaarAuthors.BazaarClient.yml" | cut -d' ' -f1)"
BAZAAR_FLATPAK_WORK="$tmp/work" BAZAAR_FLATPAK_TARBALL="$tmp/Bazaar-0.0.0-linux-x86_64.tar.gz" \
  "$here/build-flatpak.sh" --arch x86_64 --render-only > "$tmp/out.log"
after="$(sha256sum "$here/TheBazaarAuthors.BazaarClient.yml" | cut -d' ' -f1)"
[ "$before" = "$after" ] || fail "the checked-in manifest was modified"

stage="$tmp/work/stage"
rendered="$stage/TheBazaarAuthors.BazaarClient.yml"
before="$(sha256sum "$here/io.github.codex_alchemist_dev.BazaarClient.yml" | cut -d' ' -f1)"
BAZAAR_FLATPAK_WORK="$tmp/work" BAZAAR_FLATPAK_TARBALL="$tmp/Bazaar-0.0.0-linux-x86_64.tar.gz" \
  "$here/build-flatpak.sh" --arch x86_64 --render-only > "$tmp/out.log"
after="$(sha256sum "$here/io.github.codex_alchemist_dev.BazaarClient.yml" | cut -d' ' -f1)"
[ "$before" = "$after" ] || fail "the checked-in manifest was modified"

stage="$tmp/work/stage"
rendered="$stage/io.github.codex_alchemist_dev.BazaarClient.yml"
[ -f "$rendered" ] || fail "rendered manifest missing"
grep -q "sha256: '$expected'" "$rendered" || fail "rendered manifest lacks the tarball sha256"
grep -q "path: 'bazaar-client.tar.gz'" "$rendered" || fail "rendered manifest lacks the tarball path"
if grep -q '@[A-Z0-9_]*@' "$rendered"; then fail "placeholder left in the rendered manifest"; fi
for f in bazaar-client.tar.gz bazaar-client.svg TheBazaarAuthors.BazaarClient.desktop TheBazaarAuthors.BazaarClient.metainfo.xml; do
for f in bazaar-client.tar.gz bazaar-client.svg io.github.codex_alchemist_dev.BazaarClient.desktop io.github.codex_alchemist_dev.BazaarClient.metainfo.xml; do
  [ -f "$stage/$f" ] || fail "staged file missing: $f"
done
cmp -s "$tmp/Bazaar-0.0.0-linux-x86_64.tar.gz" "$stage/bazaar-client.tar.gz" || fail "staged tarball differs"

if "$here/build-flatpak.sh" --arch riscv64 --render-only >/dev/null 2>&1; then fail "unsupported arch was accepted"; fi

echo "ok: manifest rendered with sha256 $expected"
