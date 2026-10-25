#!/usr/bin/env bash
# S14 layout tick benchmark: build once in Release, then run three full passes.
# Usage: run.sh [outdir]   (default: <this dir>/results)
#
# The build runs from a temporary directory outside the repo on purpose: the repo's global.json pins
# SDK 10.0.402, which is not installed on the spike machine (10.0.401 is). The project path is passed
# explicitly, so the root Directory.Build.props still applies.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/results}"
BUILD="$(mktemp -d)"
mkdir -p "$OUT"
( cd "$BUILD" && dotnet build "$HERE/S14.Bench.csproj" -c Release -o "$BUILD/out" --nologo -v q )
for r in 1 2 3; do
  dotnet "$BUILD/out/S14.Bench.dll" "$OUT/run$r.csv" "$r"
done
