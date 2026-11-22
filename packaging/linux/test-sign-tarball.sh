#!/usr/bin/env bash
# Tests sign-tarball.sh and verify-tarball.sh with a throwaway key. Run: bash packaging/linux/test-sign-tarball.sh
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
export GNUPGHOME="$work/gnupg"
mkdir -p "$GNUPGHOME" && chmod 700 "$GNUPGHOME"
failures=0

check() { # check <name> <expected-status> <command...>
  local name="$1" want="$2"; shift 2
  "$@" >/dev/null 2>&1; local got=$?
  if [ "$got" -ne "$want" ]; then echo "FAIL $name: exit $got, expected $want"; failures=$((failures + 1)); fi
}

gpg --batch --quiet --passphrase '' --quick-generate-key "Bazaar Test <test@example.invalid>" ed25519 sign never
gpg --batch --armor --export "Bazaar Test" > "$work/public.asc"
printf 'payload' > "$work/Bazaar-0.0.0-linux-x86_64.tar.gz"
(cd "$work" && sha256sum Bazaar-0.0.0-linux-x86_64.tar.gz > Bazaar-0.0.0-linux-x86_64.tar.gz.sha256)
t="$work/Bazaar-0.0.0-linux-x86_64.tar.gz"

check "refuses to sign without a key" 2 env -u BAZAAR_SIGNING_GPG_KEY "$here/sign-tarball.sh" "$t"
check "signs with a named key" 0 env BAZAAR_SIGNING_GPG_KEY="Bazaar Test" "$here/sign-tarball.sh" "$t"
check "verifies a good signature" 0 "$here/verify-tarball.sh" --public-key "$work/public.asc" "$t"
check "signature needs a key" 2 "$here/verify-tarball.sh" "$t"
printf 'x' >> "$t"
check "rejects a modified tarball" 1 "$here/verify-tarball.sh" --public-key "$work/public.asc" "$t"
(cd "$work" && sha256sum Bazaar-0.0.0-linux-x86_64.tar.gz > Bazaar-0.0.0-linux-x86_64.tar.gz.sha256)
check "rejects a stale signature after the checksum is refreshed" 1 "$here/verify-tarball.sh" --public-key "$work/public.asc" "$t"
rm "$t.asc"
check "rejects an unsigned tarball" 1 "$here/verify-tarball.sh" --public-key "$work/public.asc" "$t"

if [ "$failures" -ne 0 ]; then echo "$failures failure(s)"; exit 1; fi
echo "all sign-tarball tests passed"
