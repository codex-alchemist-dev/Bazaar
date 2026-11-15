#!/usr/bin/env bash
# Android accessibility gate (doc 49 section 2): installs the gate app on the running emulator or device, launches it,
# and fails unless it logs RESULT=PASS. The app runs AccessibleControlScan on the Android head's pairing view
# (tests/Bazaar.Client.Android.Gate.Tests). Run by the build-android job in .github/workflows/ci.yml, inside
# reactivecircus/android-emulator-runner. Needs the .NET Android workload and adb on PATH.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

PROJECT=tests/Bazaar.Client.Android.Gate.Tests/Bazaar.Client.Android.Gate.Tests.csproj
PACKAGE=org.bazaar.client.accessibilitygate
ACTIVITY=org.bazaar.client.accessibilitygate.MainActivity
TAG=BazaarA11yGate
WAIT_SECONDS=60

adb wait-for-device
adb shell 'while [ "$(getprop sys.boot_completed | tr -d "\r")" != "1" ]; do sleep 2; done'

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
