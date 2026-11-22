#!/usr/bin/env bash
# Resolve the release channel and staged ring for a Bazaar release tag (docs/VERSIONING.md sections 1 and 4).
#
# Usage: resolve-channel.sh <tag> [ring]
#   tag   a release tag, vMAJOR.MINOR.PATCH[-pre] (for example v0.1.0 or v0.2.0-beta.1)
#   ring  optional staged ring name, default "all"
#
# Output (stdout): channel=<stable|beta|nightly> and ring=<name>, one per line.
#   The pre-release identifier decides the channel:
#     no suffix         -> stable
#     -alpha.N          -> nightly (deprecated alias: the owner's channels are stable, beta, nightly
#                          and LTS, docs/design/49 section 6; a warning goes to stderr)
#     -beta.N, -rc.N    -> beta   (rc is a beta-track release candidate)
#   The LTSC and Latest-Commit channels are never produced from a tag: LTSC lives on its own
#   branch and Latest-Commit is a CI build, not a release tag. The Bazaar.Server operator picks
#   a channel per install; this script only labels a published release.
#   LTSC releases are checked by lts-branch.sh (branch ltsc/<codename>, patch-only bumps).
#
# Exit 2 on a tag or ring that is not valid, so release workflows fail closed.

set -euo pipefail

tag="${1:-}"
ring="${2:-all}"

semver_re='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$'

if [[ ! "$tag" =~ $semver_re ]]; then
  echo "error: '$tag' is not a SemVer release tag (expected vMAJOR.MINOR.PATCH[-pre])." >&2
  exit 2
fi

if [[ ! "$ring" =~ ^[a-z][a-z0-9-]{0,31}$ ]]; then
  echo "error: ring '$ring' must be 1-32 lowercase letters, digits or hyphens, starting with a letter." >&2
  exit 2
fi

pre=""
if [[ "$tag" == *-* ]]; then
  pre="${tag#*-}"
fi

case "$pre" in
  "")                       channel="stable" ;;
  alpha.*)
    echo "warning: -alpha.N is a deprecated alias for the nightly channel." >&2
    channel="nightly" ;;
  beta.*|rc.*)              channel="beta" ;;
  *)
    echo "error: pre-release '$pre' has no channel (use alpha.N, beta.N or rc.N)." >&2
    exit 2 ;;
esac

printf 'channel=%s\nring=%s\n' "$channel" "$ring"
