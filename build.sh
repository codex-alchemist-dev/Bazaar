#!/usr/bin/env bash
# Restore, build, test and format-check the Bazaar solution. Exits non-zero on any failure.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

if [[ -d "$HOME/.dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet restore Bazaar.slnx
dotnet build Bazaar.slnx -c Release --no-restore
dotnet test --solution Bazaar.slnx -c Release --no-build
dotnet format Bazaar.slnx --verify-no-changes --no-restore
