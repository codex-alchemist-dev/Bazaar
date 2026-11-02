#!/usr/bin/env bash
# Build the Android head (src/Bazaar.Client.Android). Needs the .NET Android workload and the Android SDK.
# It is outside Bazaar.slnx on purpose: hosted CI runners have neither.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ -d "$HOME/.dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet workload install android
dotnet restore src/Bazaar.Client.Android/Bazaar.Client.Android.csproj
dotnet build src/Bazaar.Client.Android/Bazaar.Client.Android.csproj -c Release --no-restore
