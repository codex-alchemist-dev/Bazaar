#!/usr/bin/env bash
# Pull request build and test limited to the projects the change can affect (see affected.py).
# Usage: build-affected.sh "<projects>" "<test projects>"   (space separated csproj paths)
# Writes a temporary solution that lists only the affected projects, then builds, tests and format-checks that
# slice. One restore, one parallel build, MSBuild and the test runner both use every core, and the format check
# loads only these projects instead of the whole repository. Android projects are built by their own CI job.
# main, manual and scheduled runs keep the full build.sh.
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

slice="Bazaar.affected.slnx"
python3 scripts/ci/make-slice.py "$slice" $projects $tests

dotnet restore "$slice"
dotnet build "$slice" -c Release --no-restore
if [[ -n "$tests" ]]; then
  dotnet test --solution "$slice" -c Release --no-build
fi
dotnet format "$slice" --verify-no-changes --no-restore
