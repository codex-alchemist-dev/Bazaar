#!/usr/bin/env bash
# Linux client start-up gate (doc 30, testing): builds the Linux head and starts it on a virtual X display (Xvfb). The
# headless Avalonia tests cannot see a start-up that aborts on a real display, which is how the missing UseSkia() and
# UseHarfBuzz() calls reached main. Run by the linux-client-startup job in .github/workflows/ci.yml. Needs xvfb-run and
# the .NET SDK from global.json on PATH.
#
# A client that starts is still running when the timeout fires (exit 124). Any other exit code, or an unhandled
# exception in the output, fails the gate. An explicit Wayland request on an X-only session must fail, not silently
# fall back (no temporary fallbacks).
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

PROJECT=src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj
RUN_SECONDS="${BAZAAR_STARTUP_SECONDS:-20}"
KILL_AFTER_SECONDS=5
SCREEN="-screen 0 1280x800x24"
TIMED_OUT=124

command -v xvfb-run >/dev/null || { echo "xvfb-run is not installed"; exit 1; }

dotnet build "$PROJECT" -c Release -nologo -v q
dll="$(find src/Bazaar.Client.Linux/bin/Release -name Bazaar.Client.Linux.dll -print -quit)"
[ -n "$dll" ] || { echo "Bazaar.Client.Linux.dll was not built"; exit 1; }

# Runs the client once under Xvfb with the given VAR=value pairs and sets $exit_code and $log_file.
run_client() {
  log_file="$(mktemp)"
  set +e
  env "$@" timeout --kill-after="$KILL_AFTER_SECONDS" "$RUN_SECONDS" \
    xvfb-run -a -s "$SCREEN" dotnet "$dll" >"$log_file" 2>&1
  exit_code=$?
  set -e
}

expect_running() {
  local name="$1"
  shift
  run_client "$@"
  if [ "$exit_code" -ne "$TIMED_OUT" ] || grep -q "Unhandled exception" "$log_file"; then
    echo "FAIL $name: exit $exit_code, expected the client still running after ${RUN_SECONDS}s"
    cat "$log_file"
    exit 1
  fi
  echo "PASS $name: client stayed up for ${RUN_SECONDS}s"
}

expect_fails() {
  local name="$1"
  shift
  run_client "$@"
  if [ "$exit_code" -eq "$TIMED_OUT" ] || [ "$exit_code" -eq 0 ]; then
    echo "FAIL $name: exit $exit_code, expected a start-up failure"
    cat "$log_file"
    exit 1
  fi
  echo "PASS $name: start-up failed with exit $exit_code, as required"
}

expect_running "default backend"
expect_running "explicit x11" BAZAAR_DISPLAY_BACKEND=x11
expect_fails "explicit wayland on an X-only session" BAZAAR_DISPLAY_BACKEND=wayland
