# ReadyToRun (R2R) for the Bazaar client publish: repo feasibility

Status: RESEARCHED notes, 2026-11-11. Notes only. No code, doc or config was changed.
Labels: **[verified]** = primary source read (repo file, Microsoft Learn, Avalonia docs, nuget.org page read this session). **[secondary]** = blog, forum, search snippet, or third-party page. **[unverified]** = not checked here; needs the measured check in section 6.

## 1. Current state (repo facts)

**Publish is done only in two places, both in `.github/workflows/release.yml` (tag-triggered) and in packaging scripts.** [verified]

| Consumer | Command shape | RID | Self-contained | Heads | Source |
|---|---|---|---|---|---|
| release.yml `package-windows` | `dotnet publish <csproj> -c Release -r win-x64 --self-contained true -o <dir>` | win-x64 | yes | Server.Host, Client.Windows, Client.Terminal | `.github/workflows/release.yml` lines 333-354 |
| release.yml `package-linux` | same shape | linux-x64 | yes | Server.Host, Client.Linux, Client.Terminal | `.github/workflows/release.yml` lines 388-401 |
| release.yml `package-macos` (opt-in `MACOS_BUILDS`) | same shape | osx-arm64 | yes | Server.Host, Client.Terminal | `.github/workflows/release.yml` lines 436-448 |
| release.yml `package-android` | `dotnet publish ... -c Release -p:AndroidPackageFormat=apk` | none (Android workload) | n/a | Client.Android | `.github/workflows/release.yml` lines 486-492 |
| `packaging/windows-msix/build-msix.ps1` | `dotnet publish $project -c Release -r win-x64 --self-contained true -o $layout` | win-x64 | yes | Client.Windows | build-msix.ps1 line 40 |
| `packaging/linux-appimage/build-appimage.sh` | `dotnet publish ... --configuration Release --runtime <linux-x64|linux-arm64> --self-contained true` | per arch | yes | Client.Linux | build-appimage.sh lines 64-69 (not called by any workflow) |
| `packaging/linux-flatpak/TheBazaarAuthors.BazaarClient.yml` | `dotnet publish ... --runtime linux-x64 --self-contained true --source /run/build/...` (offline) | linux-x64 | yes | Client.Linux | flatpak yml lines 47-52 |
| `packaging/linux-snap/snap/snapcraft.yaml` | `dotnet publish ... -c Release -r linux-x64 --self-contained true` | linux-x64 | yes | Client.Linux | snapcraft.yaml lines 65-66 |
| `packaging/macos/build-pkg.sh` | takes an existing publish dir (example uses osx-arm64, self-contained) | osx-arm64 | yes | Client.Terminal (default entry) | build-pkg.sh lines 19-21 |

- No client publish uses PublishSingleFile, PublishTrimmed, PublishAot or PublishReadyToRun. A repo-wide grep for these and for `ReadyToRun|crossgen|mibc|TieredCompilation|InvariantGlobalization` finds only `PublishSingleFile=false` in the server packages (`packaging/linux-deb/debian/rules` line 23, `packaging/linux-rpm/bazaar-server.spec` line 71). [verified]
- Every client head is self-contained. There is no framework-dependent client build. [verified]
- `build.sh` and `build.ps1` only run `dotnet restore`, `dotnet build -c Release --no-restore`, `dotnet test`, and `dotnet format`. They never publish. [verified: `build.sh` lines 14-17, `build.ps1` lines 19-22]
- `.github/workflows/ci.yml` runs `./build.sh` / `./build.ps1` on ubuntu-latest and windows-latest and never publishes a client. So an R2R break would first appear on a tag push, not on a PR. [verified: ci.yml lines 82-89]
- `packaging/ci-release-packaging.yml` is a second release sketch that sits outside `.github/workflows/`, so GitHub never runs it. [verified: its header and location]

**Heads and libraries** (all `net11.0`) [verified: csproj files]:
- `src/Bazaar.Client.Windows/Bazaar.Client.Windows.csproj`: WinExe, `PackageReference Avalonia.Desktop`, refs Core, Components.Launcher, Components.Chat.
- `src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj`: WinExe (sic), `Avalonia.Desktop`, refs Core.
- `src/Bazaar.Client.Terminal/Bazaar.Client.Terminal.csproj`: Exe, no Avalonia, refs Core and Contracts.
- `src/Bazaar.Client.Core/Bazaar.Client.Core.csproj`: library, `Avalonia` and `Avalonia.Themes.Fluent`. No RID, no publish settings.
- `src/Bazaar.Client.Components.Launcher/...csproj`: library, no packages.
- `src/Bazaar.Client.Android/...csproj`: library on net11.0 unless `BazaarAndroid=true`, which switches to `net11.0-android` and `Avalonia.Android`. Not in `Bazaar.slnx`.

**Central package management** [verified: `Directory.Packages.props`, `Directory.Build.props`]:
- `ManagePackageVersionsCentrally=true`. `Avalonia`, `Avalonia.Desktop`, `Avalonia.Android`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Skia` and `Avalonia.Headless.XUnit` are all pinned to **12.1.4**.
- No package carries metadata for R2R. The client csproj files reference packages without a version.
- `Directory.Build.props`: `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `Deterministic=true`, `ContinuousIntegrationBuild=true` when CI=true, `VersionPrefix=0.1.0`.

**SDK pin** [verified: `global.json`]: `11.0.100-rc.2.26504.105`, `rollForward: latestPatch`, `allowPrerelease: true`. This is an RC SDK. See conflict C-1 in section 5.

**Avalonia packages** [secondary: nuget.org page read this session]: `Avalonia.Desktop` 12.1.4 lists `net8.0` and `net10.0` as included target frameworks. It depends on Avalonia, Avalonia.HarfBuzz, Avalonia.Native, Avalonia.Skia, Avalonia.Win32 and Avalonia.X11. The net11.0 project therefore consumes a net10.0 asset (inference, [unverified] for the exact resolution). The NuGet page makes no ReadyToRun, trimming or AOT claim. [unverified: Avalonia IsTrimmable or IsAotCompatible metadata.]

## 2. Feasibility verdict

**Feasible. The change is a few csproj lines per GUI head. Nothing in the repo blocks it.** Expected benefit is unmeasured.

- The Avalonia performance page says: "XPF applications can benefit significantly from ReadyToRun (R2R) compilation", and the snippet is `<PublishReadyToRun>true</PublishReadyToRun>` followed by `dotnet publish -r linux-x64 -c Release`. [verified: https://docs.avaloniaui.net/xpf/configuration/performance]
- The same page warns: "On Linux, ReadyToRun may change how native `.so` libraries are resolved." The page does not say which Avalonia version it applies to, and it does not say what the change is. [verified: same page] A packaged Linux smoke test is required (section 6).
- R2R output keeps the IL as well as native code, so reflection and XAML loading still work on the IL. [verified: https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run "binaries contain both intermediate language (IL) code ... and the native version"] This is the basis for saying no Avalonia-specific blocker exists. The reflection-compatibility point itself is [unverified] for Avalonia 12.
- The framework is already R2R-compiled in a self-contained publish. The doc says "The .NET runtime libraries have already been precompiled with ReadyToRun", and for self-contained apps the SDK precompiles "the set of assemblies that are distributed with the application... this set of assemblies will include the framework." [verified: ready-to-run page] So the gain is in Bazaar.* and in Avalonia and other managed NuGet assemblies.
- Size: "the size of an assembly will grow to between two to three times larger." [verified: ready-to-run page] Only managed DLLs grow. Native `libSkiaSharp`, `libHarfBuzzSharp` and similar are untouched [unverified: list not checked].
- A secondary source reports a hello-world Avalonia window going from about 2.4 s to about 1.4 s with R2R. [secondary: search snippet citing an Avalonia GitHub issue, unverified and hello-world only] Do not treat it as the Bazaar number.

## 3. MSBuild properties and options

### 3a. Properties to use (publish only, net11.0 only)

Verified names and behaviour [verified: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish, https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run]:
- `PublishReadyToRun` = `true`. "Compiles application assemblies as ReadyToRun (R2R) format." The publish doc recommends setting it in the project or a publish profile, not on the command line. R2R "is only available when you publish an app that targets specific runtime environments (RID)."
- `PublishReadyToRunShowWarnings` = `true`. The publish doc: "To see warnings about missing dependencies that could cause runtime failures, use `PublishReadyToRunShowWarnings=true`."
- `PublishReadyToRunEmitSymbols` = `true`. Writes `.ni.pdb` on Windows and `.r2rmap` on Linux, for profilers. Not needed for ship builds. [verified]
- `PublishReadyToRunExclude` (item). Removes named assemblies from R2R. Use only if an assembly fails. [verified]
- `PublishReadyToRunComposite` = `true`. Requires self-contained (ignored otherwise). Composite "takes significantly longer", "the application is significantly larger", and "the compiled assemblies must be distributed together." [verified]
- `PublishReadyToRunUseCrossgen2`, `PublishReadyToRunPerfmapFormatVersion`: referenced in `Microsoft.NET.Publish.targets` as parameters to `ResolveReadyToRunCompilers`. Their defaults are not visible in the file read. [secondary: summary of the SDK targets file; the file is at https://github.com/dotnet/sdk/blob/main/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Publish.targets]
- `PublishReadyToRunCrossgen2ExtraArgs`: the name appears in dotnet/runtime and MAUI pull-request snippets that append crossgen2 flags such as `--partial` and `--embed-pgo-data`. [secondary: search snippets] Argument syntax and the Release-only behaviour are [unverified].

Environment variable for A/B tests without a rebuild: `DOTNET_ReadyToRun=0` disables use of R2R code at runtime. Setting it to `1` or leaving it unset uses R2R data when present. [verified: https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation]

### 3b. Recommended condition (publish-only, net11.0, Release, RID set)

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release' And '$(TargetFramework)' == 'net11.0' And '$(RuntimeIdentifier)' != ''">
  <PublishReadyToRun>true</PublishReadyToRun>
  <PublishReadyToRunShowWarnings>true</PublishReadyToRunShowWarnings>
</PropertyGroup>
```

Why this condition:
- `PublishReadyToRun` affects only the publish pipeline. Build and test output is untouched. [verified: publish doc; secondary on the target chain]
- The RID check stops a bare `dotnet publish` from hitting the NETSDK1094 path. Without a RID, R2R cannot run. [verified: R2R page says R2R requires a RID; exact warning vs error text is [unverified]]
- Tests reference Core, not the heads, and never publish. Debug builds never publish in CI. [verified: `Bazaar.slnx` and CI]
- `-r` sets `RuntimeIdentifier` as a global property, so the condition is visible at evaluation time. [unverified: check with `dotnet msbuild -getProperty:RuntimeIdentifier`]

Do not put this in `Directory.Build.props`. That file applies to the server host, the Terminal, the tests and the Android library too, and the owner's scope is the GUI clients.

### 3c. Composite and .mibc

- **Composite** (`PublishReadyToRunComposite`): allowed here because every client head is self-contained. It is slower to build and larger. Do not enable it until a measured run shows the startup gain is worth the size. [verified constraints; gain unmeasured]
- **.mibc profile (static PGO)**: the crossgen2 command accepts `--mibc` / `-m` and `--embed-pgo-data` [verified: https://source.dot.net/crossgen2/Crossgen2RootCommand.cs.html, which lists the options; the description text is in a resource file not shown there]. Pass them through `PublishReadyToRunCrossgen2ExtraArgs` [secondary]. The exact argument syntax is [unverified].
- MAUI ships its own `.mibc` profiles and passes them to R2R [verified: https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation]. That is MAUI-specific. No Avalonia or Bazaar profile exists, and I did not search for one [unverified].
- Creating a profile needs a trace tool. I recall `dotnet-pgo` from dotnet/runtime, but that is [unverified] here.
- **Recommendation:** no .mibc in the first change. Add one only if the measured R2R run still shows JIT time as the main cost.
- `--instruction-set` exists [verified option name]. Its default and effect on the NUC are [unverified]. Do not pin an ISA without a measured need.

### 3d. Tiering interaction

- R2R code is a first tier. Hot methods are rejitted by tier 1. [verified: compilation page "The first tier ... loads pre-compiled code (ReadyToRun)"; R2R page "tiered compilation will replace commonly used ReadyToRun methods"] So steady-state speed is not expected to drop. [unverified for Bazaar]
- Do not change `TieredCompilation`, `TieredCompilationQuickJit`, `TieredPGO` or `QuickJitForLoops` in the first change. They are separate knobs. [verified names: compilation page]

## 4. Recommended file changes

| # | File | Change | Why |
|---|---|---|---|
| 1 | `src/Bazaar.Client.Windows/Bazaar.Client.Windows.csproj` | Add the section 3b PropertyGroup | Windows GUI head. Picked up by release.yml `package-windows` and `build-msix.ps1` with no workflow change. |
| 2 | `src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj` | Same PropertyGroup | Linux GUI head. Picked up by release.yml `package-linux`, `build-appimage.sh`, the Flatpak yml and `snapcraft.yaml`. Each needs the crossgen2 pack available (section 5). |
| 3 | `src/Bazaar.Client.Terminal/Bazaar.Client.Terminal.csproj` | Optional, after measurement. Not in the first change. | CLI with no Avalonia. Startup budgets in doc 28/30 are for the GUI heads only. Publish by release.yml on three RIDs. |
| 4 | `src/Bazaar.Client.Core/Bazaar.Client.Core.csproj` and `src/Bazaar.Client.Components.Launcher/*.csproj` | No change | Libraries. Their assemblies are compiled as part of the head's publish. |
| 5 | `src/Bazaar.Client.Android/Bazaar.Client.Android.csproj` | No change | Android uses the .NET for Android pipeline. The MAUI doc gives R2R rules for Android (needs `PublishTrimmed`), which do not transfer to plain Avalonia.Android [unverified]. Keep out of scope. |
| 6 | `Directory.Build.props` | No change | Would reach the server host, tests and Android. |
| 7 | `Directory.Packages.props` | No change | No package is referenced for R2R. The crossgen2 pack is pulled by the SDK. The NuGet page for `Microsoft.NETCore.App.Crossgen2.linux-x64` says it is "used by the .NET SDK" and advises against a direct reference [secondary]. |
| 8 | `global.json` | Do not change in this task. Report (C-1). | RC pin conflicts with the owner's "latest 11.0 SDK" rule. The crossgen2 pack version follows the SDK. |
| 9 | `build.sh`, `build.ps1` | No change required | They never publish. Restore runs without `-c Release` and without a RID, so the condition is false and no crossgen2 pack is fetched [inference, measure]. Rule: never add `--no-restore` to a client publish. If you do, add `-p:PublishReadyToRun=true` to the restore too. That is the NETSDK1094 break. [verified: https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/6.0/publish-readytorun-requires-restore-change] |
| 10 | `.github/workflows/ci.yml` | Add a publish-smoke job (ubuntu-latest and windows-latest) that runs `dotnet publish -c Release -r <rid> --self-contained true` for Client.Windows and Client.Linux, then checks the output. Optionally run the Linux binary under a virtual X server for a few seconds. | Today no PR path publishes a client, so an R2R break first shows on a tag. Virtual X server feasibility on the hosted runner is [unverified]. Do not add publish to `build.sh`; that slows every local run. |
| 11 | `.github/workflows/release.yml` | No change required | The publish lines use `-c Release -r ... --self-contained true`, so the csproj property applies. |
| 12 | `packaging/linux-flatpak/TheBazaarAuthors.BazaarClient.yml` and `generated-sources.json` | Must change before R2R is enabled for Linux, or the Flatpak build breaks offline. Add the crossgen2 pack for linux-x64 (and the runtime pack already needed by self-contained) to the generated sources. | The yml builds with `--source /run/build/bazaar-client/nuget-sources` and no network. `generated-sources.json` is an empty placeholder (yml comments on lines 70-72). Failure mode is inference [unverified]. |
| 13 | `packaging/linux-snap/snap/snapcraft.yaml` | No change in this task. Check the snap build downloads the crossgen2 pack. | Network build; the pack is fetched at publish. [unverified] |
| 14 | `packaging/android/build-android.sh` (line 23) and `src/Bazaar.Client.Android/Bazaar.Client.Android.csproj` (comment line 4) | Not R2R. Report only: default `ANDROID_TFM=net10.0-android` is stale against `net11.0-android`; the csproj comment points at `scripts/build-android.sh`, but the real path is `packaging/android/build-android.sh`. | [verified: both files] |
| 15 | `docs/design/28-windows-client.md` (Startup budgets, line 28) and `docs/design/30-linux-client-and-packaging.md` (section 3) | After the change lands and is measured, record the R2R setting and numbers. CLAUDE.md requires doc fixes in the same change. | Budgets are PROPOSED and not measured. |

## 5. Risks and conflicts

- **C-1 (conflict, report to owner): SDK pin.** `global.json` pins `11.0.100-rc.2.26504.105` [verified]. CLAUDE.md says "the repo pins the latest 11.0 SDK in global.json". NuGet lists `Microsoft.NETCore.App.Crossgen2.linux-x64` 11.0.0 (published 11/10/2026) after the RC builds [secondary: nuget.org; MIT licence]. The R2R pack version has to match the SDK's runtime, so the RC pin and the GA pack can disagree. Whether the SDK picks the matching pack automatically is [unverified].
- **Benefit is unmeasured.** Framework code is already R2R. The Avalonia gain is a hello-world number from a secondary source. Doc 28's budgets are not measured [verified: doc 28 line 28].
- **Size.** Managed assemblies 2-3x larger [verified]. Client archives on GitHub Releases grow. Measure the total.
- **Build time.** Publish compiles every managed assembly per RID. Amount is [unverified].
- **Linux `.so` resolution.** Avalonia warns about native lookup on Linux [verified warning, mechanism unknown]. The AppDir, tarball, Flatpak and Snap layouts all need a smoke run.
- **Warnings as errors.** `TreatWarningsAsErrors=true` is set [verified]. `PublishReadyToRunShowWarnings` may surface crossgen2 warnings that then fail the publish. Whether they are errors is [unverified].
- **Restore side effect.** Adding the property may make a publish restore fetch the crossgen2 pack even for Release. Measure with `dotnet restore` in `build.sh` (section 6).
- **Offline builds.** Flatpak (and possibly Snap and any air-gapped packager) needs the crossgen2 pack in the offline set. Section 4 row 12.
- **Cross-RID compile.** The MS table says a Windows x64 SDK can compile for `linux-x64` and `osx-arm64`, and a Linux x64 SDK can compile for `linux-arm64` [verified: R2R page cross-platform table]. The AppImage aarch64 path would use this. Not exercised in CI today [verified: CI matrix].
- **Composite.** Slower build, larger output, and output must ship together [verified]. Not in the first change.
- **Forward compatibility.** Clients are self-contained, so R2R code matches the bundled runtime. That fits the DECIDED rule "never pin to an older runtime". Framework-dependent publishes do not occur for clients, so the servicing question does not arise [verified: no framework-dependent client publish].
- **Debug and tests.** No effect, given the condition in 3b [verified condition; behaviour inferred from the publish-only doc].
- **`packaging/ci-release-packaging.yml`** is an inert second release sketch. If someone moves it into `.github/workflows/`, it duplicates `release.yml` [verified: location and header].

## 6. Facts still needing a measured check

1. Does `dotnet publish -c Release -r win-x64 --self-contained true` with the section 3b property produce R2R `Bazaar.*.dll` and Avalonia DLLs? Check with `PublishReadyToRunShowWarnings=true` and the `obj` crossgen2 response files.
2. Which crossgen2 pack version does the rc.2 SDK select, and does a GA pack match? Run `dotnet publish -v:d` and read `ResolveReadyToRunCompilers` output.
3. Does `PublishReadyToRunShowWarnings` produce warnings that `TreatWarningsAsErrors` turns into publish failures?
4. Publish size: total `du` of the publish directory, R2R on versus off, for Windows and Linux.
5. Startup A/B on the NUC (Windows) and on Linux, same binary: `DOTNET_ReadyToRun=0` versus default. Measure process start to `Main`, `Main` to window opened, and first rendered frame. Use cold runs (Linux: drop the page cache first) and warm runs. Compare with doc 28's PROPOSED budgets (1.0 s shell visible, 0.5 s dashboard from cache).
6. Working set after first frame, R2R on and off.
7. Packaged Linux smoke test (tarball, AppImage, and Flatpak if it builds) after R2R: Avalonia `.so` resolution and window creation.
8. Restore cost: does the property add a crossgen2 download to `dotnet restore` in `build.sh` and in CI? Time it.
9. Build time for `dotnet publish` with R2R on, per RID.
10. Composite versus non-composite: size and first-frame difference. Only if (5) shows a gap.
11. Whether the .mibc route helps Avalonia at all. Needs a profile tool and a trace. Only if (5) shows JIT is still dominant.
12. Whether any Avalonia 12.1.4 DLL is already R2R-compiled in the NuGet package, which changes the expected gain [unverified].
13. Whether Avalonia.Desktop 12.1.4's net10.0 asset is the one selected for net11.0, and whether a net11.0 target exists in a later Avalonia release [unverified].
14. Whether `PublishReadyToRunCrossgen2ExtraArgs` is the right property name in the SDK that the repo pins [secondary only].
15. Whether the Flatpak build passes offline with the crossgen2 pack added to `generated-sources.json` [unverified].
16. Whether a headless Linux publish smoke under a virtual X server runs on `ubuntu-latest` [unverified].

## 7. Sources

Repo files read [verified]:
- `/home/user/Bazaar/Directory.Build.props`, `/home/user/Bazaar/Directory.Packages.props`, `/home/user/Bazaar/global.json`, `/home/user/Bazaar/Bazaar.slnx`
- `/home/user/Bazaar/build.sh`, `/home/user/Bazaar/build.ps1`
- `/home/user/Bazaar/src/Bazaar.Client.Windows/Bazaar.Client.Windows.csproj`, `.../Program.cs`
- `/home/user/Bazaar/src/Bazaar.Client.Linux/Bazaar.Client.Linux.csproj`, `.../Program.cs`
- `/home/user/Bazaar/src/Bazaar.Client.Core/Bazaar.Client.Core.csproj`
- `/home/user/Bazaar/src/Bazaar.Client.Components.Launcher/Bazaar.Client.Components.Launcher.csproj`, `.../LaunchPipe.cs`
- `/home/user/Bazaar/src/Bazaar.Client.Terminal/Bazaar.Client.Terminal.csproj`, `/home/user/Bazaar/src/Bazaar.Client.Android/Bazaar.Client.Android.csproj`, `/home/user/Bazaar/src/Bazaar.Server.Host/Bazaar.Server.Host.csproj`
- `/home/user/Bazaar/.github/workflows/release.yml`, `/home/user/Bazaar/.github/workflows/ci.yml`
- `/home/user/Bazaar/packaging/ci-release-packaging.yml`
- `/home/user/Bazaar/packaging/linux-appimage/build-appimage.sh`, `/home/user/Bazaar/packaging/windows-msix/build-msix.ps1`, `/home/user/Bazaar/packaging/linux-flatpak/TheBazaarAuthors.BazaarClient.yml`, `/home/user/Bazaar/packaging/linux-snap/snap/snapcraft.yaml`, `/home/user/Bazaar/packaging/macos/build-pkg.sh`, `/home/user/Bazaar/packaging/android/build-android.sh`
- `/home/user/Bazaar/packaging/windows-msi/Bazaar.Server.Windows.wixproj`, `/home/user/Bazaar/packaging/linux-deb/debian/rules`, `/home/user/Bazaar/packaging/linux-rpm/bazaar-server.spec`, `/home/user/Bazaar/packaging/scoop/bazaar-client.json`
- `/home/user/Bazaar/docs/design/28-windows-client.md`, `/home/user/Bazaar/docs/design/30-linux-client-and-packaging.md`, `/home/user/Bazaar/docs/design/37-distribution-matrix-and-packaging.md`
- `/home/user/Bazaar/docs/research/notes/Bazaar remaining gaps research/distribution_os_integration.md` (lines 29, 35: self-contained, AOT and trimming for Avalonia and Velopack were not verified)
- `/home/user/Bazaar/docs/spikes/S6.md` (line 43: AOT/trim warnings on Google.Protobuf; not an R2R source)

Web sources:
- [verified] ReadyToRun deployment overview: https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run
- [verified] dotnet publish command reference: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish
- [verified] Breaking change, publish ReadyToRun with --no-restore (NETSDK1094): https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/6.0/publish-readytorun-requires-restore-change
- [verified] Compilation config settings (DOTNET_ReadyToRun, tiered, TieredPGO): https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation
- [verified] .NET MAUI runtimes and compilation (MAUI-specific R2R and mibc): https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation
- [verified] Avalonia XPF performance: https://docs.avaloniaui.net/xpf/configuration/performance
- [verified] crossgen2 option list: https://source.dot.net/crossgen2/Crossgen2RootCommand.cs.html (option names only; descriptions are in a resource file not read)
- [secondary] Microsoft.NETCore.App.Crossgen2.linux-x64 on nuget.org (11.0.0 published 11/10/2026; MIT): https://www.nuget.org/packages/microsoft.netcore.app.crossgen2.linux-x64
- [secondary] Avalonia.Desktop 12.1.4 on nuget.org (net8.0 and net10.0 assets): https://www.nuget.org/packages/Avalonia.Desktop/12.1.4
- [secondary] SDK Publish.targets summary: https://github.com/dotnet/sdk/blob/main/src/Tasks/Microsoft.NET.Build.Tasks/targets/Microsoft.NET.Publish.targets (fetched through a summarizer; the raw file path returned 404 for the Strings.resx attempt)
- [secondary] Search snippets citing dotnet/runtime and MAUI pull requests for `PublishReadyToRunCrossgen2ExtraArgs`, `--embed-pgo-data`, `--partial`: https://github.com/dotnet/maui/pull/34660 and https://github.com/dotnet/runtime/pull/124604 (not opened)
- [secondary] Community benchmark, Avalonia hello-world 2.4 s to 1.4 s: search snippet only, original issue not opened
- [secondary] Native AOT vs ReadyToRun vs JIT in .NET 11 (single author, ASP.NET Core benchmark, not Avalonia): https://startdebugging.net/2026/05/native-aot-vs-readytorun-vs-jit-in-dotnet-11/
- [unverified] Crossgen2 mibc argument syntax, `dotnet-pgo` tool, NETSDK1094/NETSDK1095 text for the no-RID case, Avalonia IsTrimmable/IsAotCompatible metadata.
