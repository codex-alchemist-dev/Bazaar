#!/usr/bin/env bash
# Android accessibility gate (doc 49 section 2): installs the gate app on the running emulator or device, launches it,
# and fails unless it logs RESULT=PASS. The app runs AccessibleControlScan on the Android head's pairing view and
# phone shell (tests/Bazaar.Client.Android.Gate.Tests). Run by the build-android job in .github/workflows/ci.yml, inside
# reactivecircus/android-emulator-runner. Needs the .NET Android workload and adb on PATH.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

PROJECT=tests/Bazaar.Client.Android.Gate.Tests/Bazaar.Client.Android.Gate.Tests.csproj
PACKAGE=org.bazaar.client.accessibilitygate
ACTIVITY=org.bazaar.client.accessibilitygate.MainActivity
TAG=BazaarA11yGate
WAIT_SECONDS=60
DEVICE_WAIT_SECONDS=600
BOOT_WAIT_SECONDS=900
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

timeout "$DEVICE_WAIT_SECONDS" adb wait-for-device \
  || fail_with_emulator_state "no device attached within ${DEVICE_WAIT_SECONDS}s"
timeout "$BOOT_WAIT_SECONDS" adb shell 'while [ "$(getprop sys.boot_completed | tr -d "\r")" != "1" ]; do sleep 2; done' \
  || fail_with_emulator_state "device did not finish booting within ${BOOT_WAIT_SECONDS}s"

# -t:Install builds the Release app and installs it on the connected device.
dotnet build "$PROJECT" -c Release -p:BazaarAndroid=true -t:Install

adb logcat -c
adb shell am force-stop "$PACKAGE" || true
adb shell am start -W -n "$PACKAGE/$ACTIVITY"

result=""
for _ in $(seq 1 "$WAIT_SECONDS"); do
  result=$(adb logcat -d -s "$TAG:*" | grep -o 'RESULT=[A-Z]*' | tail -1 || true)
  if [ -n "$result" ]; then
    break
  fi
  sleep 1
done

adb logcat -d -s "$TAG:*" || true

if [ "$result" != "RESULT=PASS" ]; then
  echo "Android accessibility gate failed: ${result:-no result logged within ${WAIT_SECONDS}s}"
  exit 1
fi
echo "Android accessibility gate: PASS"
