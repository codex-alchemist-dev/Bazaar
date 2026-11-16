#!/usr/bin/env bash
# Android accessibility gate (doc 49 section 2): installs the gate app on the running emulator or device, launches it,
# and fails unless it logs RESULT=PASS. The app runs AccessibleControlScan on the Android head's pairing view and
# phone shell (tests/Bazaar.Client.Android.Gate.Tests). Run by the build-android job in .github/workflows/ci.yml, inside
# reactivecircus/android-emulator-runner. Needs the .NET Android workload and adb on PATH.
#
# Flakiness fix (2026-11-16): the first launch on the software-rendered emulator can take longer than 60s to log its
# result (run 43812805820: `am start` returned at 6s, nothing was logged within 60s, and the diagnostics filtered out
# any crash). The gate now waits longer per attempt, retries a launch that logs nothing, and prints crash and process
# state when an attempt logs nothing. A logged RESULT=FAIL still fails at once, and only RESULT=PASS passes.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

PROJECT=tests/Bazaar.Client.Android.Gate.Tests/Bazaar.Client.Android.Gate.Tests.csproj
PACKAGE=org.bazaar.client.accessibilitygate
ACTIVITY=org.bazaar.client.accessibilitygate.MainActivity
TAG=BazaarA11yGate
WAIT_SECONDS=180        # per attempt, measured from the launch
ATTEMPTS=3              # launches tried when an attempt logs no result at all
RETRY_PAUSE_SECONDS=10
DEVICE_WAIT_SECONDS=600
BOOT_WAIT_SECONDS=900
PACKAGE_MANAGER_WAIT_SECONDS=300
EMULATOR_LOG="${RUNNER_TEMP:-/tmp}/emulator.log"

# Bounded waits: without them a missing emulator hangs the job until the runner cancels it, and the emulator log is
# never shown. On timeout, print the emulator log and the adb device list, then fail.
fail_with_emulator_state() {
  echo "Android accessibility gate failed: $1"
  adb devices || true
  if [ -f "$EMULATOR_LOG" ]; then
    echo "--- emulator log (tail) ---"
    tail -n 200 "$EMULATOR_LOG" || true
  fi
  exit 1
}

# Prints everything that can explain a launch that logged no result: whether the process is alive, whether the activity
# is on screen, and any crash or error lines from any tag (the gate-tag filter alone hides crashes).
print_launch_diagnostics() {
  echo "--- process and activity state ---"
  adb shell pidof "$PACKAGE" || echo "process $PACKAGE is not running"
  adb shell dumpsys activity activities 2>/dev/null | grep -F "$PACKAGE" | head -n 5 || true
  echo "--- crash and error lines (tail) ---"
  adb logcat -d -v time 2>/dev/null \
    | grep -E "AndroidRuntime|$TAG|$PACKAGE| E/| F/| E | F " \
    | tail -n 200 || true
}

# Launches the gate app once and waits up to WAIT_SECONDS for a RESULT line. Sets $result. Returns 1 when no result came.
run_attempt() {
  result=""
  adb logcat -c
  adb shell am force-stop "$PACKAGE" || true
  adb shell am start -W -n "$PACKAGE/$ACTIVITY" || true

  for _ in $(seq 1 "$WAIT_SECONDS"); do
    result=$(adb logcat -d -s "$TAG:*" | grep -o 'RESULT=[A-Z]*' | tail -1 || true)
    if [ -n "$result" ]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

timeout "$DEVICE_WAIT_SECONDS" adb wait-for-device \
  || fail_with_emulator_state "no device attached within ${DEVICE_WAIT_SECONDS}s"
timeout "$BOOT_WAIT_SECONDS" adb shell 'while [ "$(getprop sys.boot_completed | tr -d "\r")" != "1" ]; do sleep 2; done' \
  || fail_with_emulator_state "device did not finish booting within ${BOOT_WAIT_SECONDS}s"
# sys.boot_completed is set before the package manager answers, so wait for it before installing.
timeout "$PACKAGE_MANAGER_WAIT_SECONDS" adb shell 'until pm path android >/dev/null 2>&1; do sleep 2; done' \
  || fail_with_emulator_state "package manager did not answer within ${PACKAGE_MANAGER_WAIT_SECONDS}s"

# -t:Install builds the Release app and installs it on the connected device.
dotnet build "$PROJECT" -c Release -p:BazaarAndroid=true -t:Install

for attempt in $(seq 1 "$ATTEMPTS"); do
  echo "Android accessibility gate: launch attempt $attempt of $ATTEMPTS"
  if run_attempt; then
    break
  fi
  echo "Android accessibility gate: attempt $attempt logged no result within ${WAIT_SECONDS}s"
  print_launch_diagnostics
  if [ "$attempt" -lt "$ATTEMPTS" ]; then
    sleep "$RETRY_PAUSE_SECONDS"
  fi
done

adb logcat -d -s "$TAG:*" || true

if [ -z "$result" ]; then
  echo "Android accessibility gate failed: no result logged after ${ATTEMPTS} launches of ${WAIT_SECONDS}s each"
  exit 1
fi
if [ "$result" != "RESULT=PASS" ]; then
  echo "Android accessibility gate failed: $result"
  exit 1
fi
echo "Android accessibility gate: PASS"
