#!/bin/bash
# Usage (inside WSL): run.sh <runN>   -- runs the spike under Xvfb (X11 backend, no WSLg dependency)
set -e
cd "$(dirname "$0")"
export DOTNET_ROOT=/opt/dotnet11 PATH=/opt/dotnet11:$PATH DOTNET_CLI_HOME=/mnt/v/TOOL/dotnet-home NUGET_PACKAGES=/mnt/v/TOOL/nuget DOTNET_NOLOGO=1
export GDK_BACKEND=x11 WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 NO_AT_BRIDGE=1
BIN=/tmp/s13-build
[ -f $BIN/S13.dll ] || dotnet build -c Release -o $BIN -v q /p:UseAppHost=false >/dev/null
xvfb-run -a -s "-screen 0 1280x800x24" dotnet $BIN/S13.dll --auto --out "${1:-results/linux-run.json}"
