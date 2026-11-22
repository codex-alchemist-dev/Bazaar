#!/usr/bin/env bash
# Pull request build and test limited to the projects the change can affect (see affected.py).
# Usage: build-affected.sh "<projects>" "<test projects>"   (space separated csproj paths)
# Restores the whole solution (the format check needs it), builds the affected projects, runs the affected
# tests, then runs the same format check as build.sh. Android projects are built by their own CI job.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

projects="${1:-}"
tests="${2:-}"

if [[ -d "$HOME/.dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet restore Bazaar.slnx
for p in $projects; do
  case "$p" in *Android*) continue;; esac
  dotnet build "$p" -c Release --no-restore
done
for t in $tests; do
  dotnet test --project "$t" -c Release --no-build
done
dotnet format Bazaar.slnx --verify-no-changes --no-restore
