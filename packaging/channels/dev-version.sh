#!/usr/bin/env bash
# Prints the Latest-Commit (nightly) version for a main build, per docs/VERSIONING.md
# section 4: vX.Y.Z-dev.N+sha. Never a tag; the string is for display and feeds only.
#
# usage: dev-version.sh <Directory.Build.props path> <build-number> <commit-sha>
# prints: version=<X.Y.Z-dev.N+sha7>  (also semver=<X.Y.Z-dev.N> without build metadata)
# exits 2 on a missing or non-SemVer VersionPrefix, a pre-release VersionPrefix,
# a build number that is not a positive integer, or a commit that is not 7+ hex digits.
set -euo pipefail

props="${1:-}"
build="${2:-}"
sha="${3:-}"

if [ -z "$props" ] || [ ! -f "$props" ]; then
  echo "dev-version: props file not found: '$props'" >&2
  exit 2
fi
if [[ ! "$build" =~ ^[1-9][0-9]*$ ]]; then
  echo "dev-version: build number must be a positive integer, got '$build'" >&2
  exit 2
fi
if [[ ! "$sha" =~ ^[0-9a-fA-F]{7,40}$ ]]; then
  echo "dev-version: commit must be 7 to 40 hex digits, got '$sha'" >&2
  exit 2
fi

prefix="$(sed -n 's:.*<VersionPrefix>\([^<]*\)</VersionPrefix>.*:\1:p' "$props" | head -n 1)"
if [[ ! "$prefix" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "dev-version: VersionPrefix must be plain MAJOR.MINOR.PATCH, got '$prefix'" >&2
  exit 2
fi

short="$(printf '%s' "${sha:0:7}" | tr 'A-F' 'a-f')"
echo "semver=${prefix}-dev.${build}"
echo "version=${prefix}-dev.${build}+${short}"
