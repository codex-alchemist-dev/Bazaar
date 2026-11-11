# Android packaging notes

Scope: the Bazaar.Client.Android head (doc 29). Status: the project does not exist in `src/` yet, so `build-android.sh` exits with code 2 and builds nothing.

## Files
- `build-android.sh`: builds an AAB and an APK with `dotnet publish`. Signed when `ANDROID_KEYSTORE_PATH` is set; unsigned otherwise. Copies outputs to `artifacts/android/` with a `signed-` or `unsigned-` prefix and writes `SHA256SUMS.txt`.

## Signing rules
- No keystore, password or key material is committed. Keep them in the CI secret store and pass them as environment variables.
- Passwords are passed to MSBuild as `env:` references, so they do not appear on the command line.
- A keystore path with missing values is an error. The script never falls back to unsigned silently.
- Play takes an AAB signed with the upload key; Play App Signing holds the app signing key (doc 30). Unsigned output is for CI checks only.

## Prerequisites (not verified in this environment)
- .NET 10 SDK and the .NET for Android workload (`dotnet workload install android`).
- Android SDK and JDK as required by the workload.
- Minimum API 35, target API 37 (issue #423; pinned by `TargetPlatformVersion` in the Android project).

## Unverified
- The `AndroidPackageFormats` value syntax (`aab;apk`) and the `env:` password form have not been tested against a real .NET for Android build.
- `.gitignore` does not yet exclude `artifacts/` or keystore files; that change is outside `packaging/android/` and was not made.
- Doc `37-distribution-matrix-and-packaging.md` is referenced by the task but is not present in this worktree (`docs/design/` ends at 36 plus 30's packaging notes). Packaging decisions here come from docs 29 and 30 and the research note `distribution_os_integration.md`.
- Nothing has been built or signed.
