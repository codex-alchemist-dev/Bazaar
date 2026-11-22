#!/usr/bin/env bash
# Verifies a Linux client tarball: the SHA-256 file, then the detached signature against a public key the verifier
# already trusts (doc 30, section 7). The release gate runs this before a draft release is created.
# Usage: verify-tarball.sh [--checksum-only] [--public-key <key.asc>] <tarball>
set -euo pipefail

checksum_only=0
public_key=""
while [ $# -gt 1 ]; do
  case "$1" in
    --checksum-only) checksum_only=1; shift ;;
    --public-key) public_key="${2:?--public-key needs a file}"; shift 2 ;;
    *) echo "unknown option $1"; exit 2 ;;
  esac
done
tarball="${1:?usage: verify-tarball.sh [--checksum-only] [--public-key <key.asc>] <tarball>}"

[ -f "$tarball" ] || { echo "$tarball not found"; exit 1; }
[ -f "$tarball.sha256" ] || { echo "$tarball.sha256 not found"; exit 1; }
(cd "$(dirname "$tarball")" && sha256sum --check --strict "$(basename "$tarball").sha256")
[ "$checksum_only" -eq 0 ] || exit 0

[ -n "$public_key" ] || { echo "--public-key is required to verify the signature"; exit 2; }
[ -f "$tarball.asc" ] || { echo "$tarball.asc not found: the tarball is not signed"; exit 1; }

home="$(mktemp -d)"
trap 'rm -rf "$home"' EXIT
export GNUPGHOME="$home"
gpg --batch --quiet --import "$public_key"
gpg --batch --status-fd 1 --verify "$tarball.asc" "$tarball" 2>/dev/null | grep -q '^\[GNUPG:\] VALIDSIG ' \
  || { echo "signature verification FAILED for $tarball"; exit 1; }
echo "signature OK: $tarball"
