# Android packaging notes

Scope: the Bazaar.Client.Android head (doc 29), the Play closed-test AAB, and the sideload APK. The head exists in `src/Bazaar.Client.Android`, so the script builds it. Docs section this implements: `docs/design/29-android-client.md` (work package 1, CI build, and work package 13, closed-test signing) and `docs/design/37-distribution-matrix-and-packaging.md` section 7 (Play account parked).

## Files
- `build-android.sh`: builds an AAB and an APK with `dotnet publish -p:BazaarAndroid=true -f net11.0-android`. Signed when `ANDROID_KEYSTORE_PATH` is set; unsigned otherwise. Copies outputs to `artifacts/android/` with a `signed-` or `unsigned-` prefix (stale ones are removed first) and writes `SHA256SUMS.txt`.
- `test-build-android.sh`: runs the script against a fake `dotnet` and checks the arguments, signing rules, versionCode and exit codes. No SDK or workload needed. CI runs it before the build.

## Version codes
- Play needs a `versionCode` that rises with every upload. The script passes `ApplicationVersion` = `ANDROID_VERSION_CODE`, or the commit count of HEAD when that is unset. The commit count rises on `main`. CI checks out with `fetch-depth: 0` so the count is real.
- The display version is `VersionPrefix` (SemVer, `docs/VERSIONING.md`); Play's version name takes it unchanged.

## Running it
- Unsigned (CI and local checks): `bash packaging/android/build-android.sh`. Needs the .NET SDK from `global.json`, the Android workload (`dotnet workload install android`) and the Android SDK and JDK.
- Signed (release, or a local upload build): set `ANDROID_KEYSTORE_PATH`, `ANDROID_KEY_ALIAS`, `ANDROID_KEYSTORE_PASSWORD` and `ANDROID_KEY_PASSWORD`. The keystore comes from the CI secret store, never from the repo.

## Signing rules
- No keystore, password or key material is committed. Keep them in the CI secret store and pass them as environment variables.
- Passwords are passed to MSBuild as `env:` references, so they do not appear on the command line.
- A keystore path with missing values is an error. The script never falls back to unsigned silently.
- Play takes an AAB signed with the upload key; Play App Signing holds the app signing key (doc 30). Unsigned output is for CI checks only.

## Prerequisites
- .NET 11 SDK (pinned in `global.json`) and the .NET for Android workload (`dotnet workload install android`).
- Android SDK and JDK as required by the workload.
- Minimum API 35, target API 37 (issue #423; pinned by `TargetPlatformVersion` in the Android project).

## Not yet verified
- The real AAB build and the `env:` password form have not run against a .NET 11 Android build in the cloud session (no SDK or workload there). The CI job `build-android` is the first real check. The shell tests cover the script's own logic only.
- The release workflow (`.github/workflows/release.yml`, `package-android`) still passes `AndroidPackageFormat=apk` (singular). That is not this script's path; the release thread should align it with `build-android.sh`.
- Nothing has been signed. No keystore exists, and the Google Play developer account is parked (doc 37 section 7).
