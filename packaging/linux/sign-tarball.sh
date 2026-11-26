#!/usr/bin/env bash
# Signs a Linux client tarball (doc 30, section 7: SHA-256 sums plus a detached signature). Writes <tarball>.asc, an
# ASCII-armoured detached OpenPGP signature. The signing key is the operator's: it is named by BAZAAR_SIGNING_GPG_KEY
# (a key id or fingerprint in the signer's keyring) and never lives in the repository. With no key named the script
# fails; an unsigned build is simply not run through this script (no silent skip).
# Usage: sign-tarball.sh <tarball>
set -euo pipefail

tarball="${1:?usage: sign-tarball.sh <tarball>}"
key="${BAZAAR_SIGNING_GPG_KEY:-}"
[ -n "$key" ] || { echo "BAZAAR_SIGNING_GPG_KEY is not set; refusing to sign without a named key"; exit 2; }
[ -f "$tarball" ] || { echo "$tarball not found"; exit 1; }
[ -f "$tarball.sha256" ] || { echo "$tarball.sha256 not found; build with build-tarball.sh first"; exit 1; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$here/verify-tarball.sh" --checksum-only "$tarball"

rm -f "$tarball.asc"
gpg --batch --yes --local-user "$key" --armor --detach-sign --output "$tarball.asc" "$tarball"
echo "signed $tarball.asc"
