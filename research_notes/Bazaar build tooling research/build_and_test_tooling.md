# Bazaar .NET 10 build, test and project-structure setup (as of 2026-10-24)

Labels: [V] = primary source read by fetch; [S] = secondary (search snippet, third-party, or aggregator); [U] = unverified (my recommendation or prior knowledge, not checked against a source). Tool budget was about 20 calls; several Microsoft and Avalonia pages returned 404 or were not reached, listed under Gaps.

Key sources used:
- xUnit v3 getting started: https://xunit.net/docs/getting-started/v3/getting-started [V]
- dotnet test (VSTest vs MTP modes): https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test [V]
- dotnet new sln defaults to SLNX in .NET 10: https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default [V]
- SDK MSBuild props: https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props [V]
- Avalonia headless concepts: https://docs.avaloniaui.net/docs/concepts/headless/ [V]
- Avalonia headless with xUnit: https://docs.avaloniaui.net/docs/testing/headless-xunit [V]
- Avalonia.Headless.XUnit on NuGet: https://www.nuget.org/packages/Avalonia.Headless.XUnit [V]
- Worker Service as Windows Service: https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service [V]

---

## 1. Solution format, global.json, Directory.Build.props, Directory.Packages.props

### Takeaway
Use `.slnx` (the .NET 10 SDK default for `dotnet new sln`), pin the SDK in `global.json` with the MTP test runner, turn on central package management in `Directory.Packages.props`, and put shared settings in `Directory.Build.props`.

### Cited Findings
- In .NET 10, `dotnet new sln` creates an SLNX (XML) solution file; the opt-out is `dotnet new sln --format sln`. Support for SLNX was added in .NET SDK 9.0.200. — [MS breaking-change page](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default) [V]
- `ManagePackageVersionsCentrally` set to `true` in `Directory.Packages.props` at the repo root, with `<PackageVersion Include=... Version=... />` items; project files then omit `Version` on `PackageReference`. Introduced in .NET 7. — [MSBuild props](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props) [V]
- `AnalysisLevel`: if the project targets .NET 5+, the default is `latest` (latest released analyzers). Set e.g. `8` to pin rules to an earlier release. — [MSBuild props](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props) [V]
- `EnforceCodeStyleInBuild=true` turns on .NET code-style analysis during build (off by default). — [MSBuild props](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props) [V]
- Setting `TestingPlatformDotnetTestSupport` in `Directory.Build.props` is recommended for the older VSTest-mode setup (see section 2), so all test projects agree. — [dotnet test doc](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test) [V]

### Inferences
- Bazaar should not add a `.sln` next to a `.slnx`; use one format. Use `.slnx` unless a tool needs `.sln`.
- `global.json` should pin the SDK so the container and the Windows machines build with the same .NET 10 SDK (10.0.402 is the container version).

### Gaps
- Exact `global.json` `sdk` block (rollForward value) not verified against a Microsoft page. Recommended content [U]:
```json
{
  "sdk": { "version": "10.0.402", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```
- The `test.runner` key is [V] (section 2). The `sdk` block is [U].
- `TreatWarningsAsErrors` has no dedicated entry on the MSBuild props page I read; the page only notes implicit-build optimization is skipped when it is `true`. Use `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in `Directory.Build.props` [U, standard MSBuild property].
- `CentralPackageVersionOverrideEnabled` not found on the pages read [unverified].
- The `.slnx` XML content for project entries (`<Project Path="..."/>`, `<Folder Name="...">`) was not read from a source [U].

### Proposed file contents [U unless marked]
`Directory.Build.props` (repo root):
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```
Note: `ManagePackageVersionsCentrally` belongs in `Directory.Packages.props` per the Microsoft text [V]; placing it in `Directory.Build.props` is a common pattern [U]. Use the `Directory.Packages.props` location to match the source.

`Directory.Packages.props` (repo root), versions shown are the ones found in this note; pin after restore:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.0-pre.108" />
    <PackageVersion Include="Avalonia.Headless.XUnit" Version="12.1.4" />
    <PackageVersion Include="Avalonia.Themes.Fluent" Version="12.1.4" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Systemd" Version="10.0.12" />
  </ItemGroup>
</Project>
```
Version notes: `Microsoft.Extensions.Hosting` 10.0.12 and `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12 are from the MS Worker Service page [V]; `Systemd` version [U]; `Avalonia.Headless.XUnit` 12.1.4 [V NuGet page]; `Avalonia.Themes.Fluent` 12.1.4 [U, same line as Headless]; `xunit.v3.mtp-v2` 4.0.0-pre.108 [V xUnit page].

### Version conflict to resolve (important)
- The xUnit page gives `xunit.v3.mtp-v2` 4.0.0-pre.108 [V].
- `Avalonia.Headless.XUnit` 12.1.4 depends on `xunit.v3.extensibility.core` >= 3.2.2 [V NuGet page].
- These are different xUnit v3 lines (3.x stable vs 4.0 prerelease). Pick one line and check with `dotnet restore` before scaffolding. Likely safer: use the stable xUnit v3 line matching `xunit.v3.extensibility.core` 3.2.x [U]; confirm the exact `xunit.v3.mtp-v2` 3.2.x version on NuGet.

---

## 2. xUnit v3 on .NET 10

### Takeaway
Use `xunit.v3.mtp-v2` on Microsoft Testing Platform (MTP). On .NET 10 SDK, add `"test": { "runner": "Microsoft.Testing.Platform" }` to `global.json`; then `dotnet test` drives MTP directly and the `TestingPlatformDotnetTestSupport` property is not needed.

### Cited Findings
- Core package: `xunit.v3.mtp-v2`, version `4.0.0-pre.108`, described as "the core package needed to write unit tests for xUnit.net v3". Template package `xunit.v3.templates` (same version); install with `dotnet new install xunit.v3.templates`. Console tool: `dotnet tool install xunit-console-tool --global` (`--prerelease` for prereleases; requires .NET 10 SDK or later). — [xUnit getting started](https://xunit.net/docs/getting-started/v3/getting-started) [V]
- Default csproj shown by xUnit: `<OutputType>Exe</OutputType>`, `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` ("used for .NET 8/9 SDK"), `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />`, `<Using Include="Xunit" />`. The page's TFM example is net8.0; change to net10.0. — [xUnit getting started](https://xunit.net/docs/getting-started/v3/getting-started) [V]
- `dotnet test` has two modes. VSTest mode is the default and is the only mode before .NET 10 SDK; MTP projects run there only with `TestingPlatformDotnetTestSupport=true` and the `Microsoft.Testing.Platform.MSBuild` package. MTP mode was introduced with the .NET 10 SDK: add `"test": {"runner": "Microsoft.Testing.Platform"}` to `global.json`. In MTP mode the property and the extra `--` are not required. This mode requires MTP 1.7 or later. — [dotnet test doc](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test) [V]
- MTP mode path arguments: `dotnet test --project X.csproj`, `dotnet test --solution X.sln`, `dotnet test --test-modules path.dll`. Test app args: `dotnet test --report-trx` (MTP mode) or `dotnet test -- --report-trx` (VSTest mode); `--report-trx` needs the `Microsoft.Testing.Extensions.TrxReport` package. — [dotnet test doc](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test) [V]
- Solutions mixing frameworks or extensions can break on unknown options (exit code 5); route arguments with `TestingPlatformCommandLineArguments` conditions. — [dotnet test doc](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test) [V]
- xUnit page says the template also generates `global.json` for .NET 10+ to use MTP. — [xUnit getting started](https://xunit.net/docs/getting-started/v3/getting-started) [V]

### Inferences
- For Bazaar, use MTP mode everywhere (global.json, not the property). Do not mix VSTest and MTP in one solution (the doc says that is unsupported).
- Each test project should be `Microsoft.NET.Sdk` with `<OutputType>Exe</OutputType>`, because xUnit v3 MTP test apps are executables.

### Minimal test project (proposed, based on the xUnit page) [U for the trimmed form]
`tests/Bazaar.Contracts.Tests/Bazaar.Contracts.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3.mtp-v2" />
    <ProjectReference Include="..\..\src\Bazaar.Contracts\Bazaar.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```
Note: `IsTestProject` is a common SDK property [U]; `PackageReference` has no `Version` because CPM is on.

`global.json` (repo root): include the `"test"` block shown in section 1.

Run: `dotnet test` from the repo root (MTP mode via global.json) [V for behavior, U for exact invocation in this repo].

### Gaps
- `IsTestingPlatformApplication` was not mentioned on the pages read [unverified].
- Whether `xunit.v3.mtp-v2` 4.0 prerelease works with `dotnet test` MTP mode in the 10.0.402 SDK was not tested (no install allowed).

---

## 3. Avalonia.Headless with xUnit v3

### Takeaway
Use `Avalonia.Headless.XUnit` (includes Avalonia.Headless and xUnit v3 extensibility). Latest stable seen: 12.1.4. Test code uses `[AvaloniaFact]` and `[AvaloniaTestApplication]`, with a test `AppBuilder` using `UseHeadless`.

### Cited Findings
- NuGet page: latest stable `12.1.4` (shown as released 10/9/2026); no prerelease visible. Targets net8.0 and net10.0. Dependencies: `Avalonia.Headless` >= 12.1.4 and `xunit.v3.extensibility.core` >= 3.2.2. — [NuGet Avalonia.Headless.XUnit](https://www.nuget.org/packages/Avalonia.Headless.XUnit) [V]
- Headless docs: packages `Avalonia.Headless.XUnit` (which also includes Avalonia) and `Avalonia.Themes.Fluent` ("even headless controls need a theme"). Create the xUnit project following xunit.net's guide. — [Avalonia headless XUnit](https://docs.avaloniaui.net/docs/testing/headless-xunit) [V]
- Test assembly attribute: `[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`, once per project. Builder: `public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());`. Use `[AvaloniaFact]` instead of `[Fact]` and `[AvaloniaTheory]` instead of `[Theory]`. Default isolation `PerTest`; `[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]` shares state and does not run tests concurrently. — [Avalonia headless XUnit](https://docs.avaloniaui.net/docs/testing/headless-xunit) [V]
- Rendering: set `UseHeadlessDrawing = false` and call `.UseSkia()`; then `window.Show(); var frame = window.CaptureRenderedFrame(); frame.Save("output.png");` (`CaptureRenderedFrame` returns a `WriteableBitmap`, which can be locked for pixel comparison). The default `UseHeadlessDrawing = true` produces no pixels. — [Avalonia headless concepts](https://docs.avaloniaui.net/docs/concepts/headless/) [V]
- Search snippet says the Headless.XUnit docs cover the current v12 guide; the search did not confirm xUnit v3 directly. — [WebSearch result, NuGet listing](https://www.nuget.org/packages/Avalonia.Headless.XUnit) [S]

### Inferences
- The xUnit v3 dependency of `Avalonia.Headless.XUnit` 12.1.4 means the test projects must use xUnit v3 (not v2). This matches the owner's choice.
- Skia needs native binaries; the headless pixel test path should run on Linux in the container [U].

### Gaps
- Exact `Avalonia.Headless.XUnit` csproj snippet and whether `xunit.v3.mtp-v2` 4.0 pre is compatible with its `xunit.v3.extensibility.core` >= 3.2.2 dependency: not verified. Resolve with restore (see section 1 conflict).
- Whether `UseSkia()` needs extra package names (e.g. `Avalonia.Skia`) in v12: not verified.
- Whether headless UI tests run without any display server on Linux: Avalonia docs describe headless platform, but a Linux-container run was not tested [U].

---

## 4. Avalonia 12 multi-platform structure and templates

### Takeaway
Use `dotnet new avalonia.app` for the head template and build a shared `Bazaar.Client.Core` library; each head references it. Android needs the `android` workload and a JDK. Template details are only partly verified.

### Cited Findings
- `Avalonia.Templates` is the template package; NuGet latest seen is 12.1.0; install with `dotnet new install Avalonia.Templates@12.1.0`; create with `dotnet new avalonia.app -o MyApp`. Requirement shown: .NET 8.0 or later. — [Avalonia install](https://docs.avaloniaui.net/docs/get-started/install) and [NuGet Avalonia.Templates](https://www.nuget.org/packages/Avalonia.Templates) [S, search snippets, not read in full]
- Avalonia docs: the `avalonia.app` template and the `-av` / `--avalonia-version` option choose the Avalonia version. — [Avalonia template page](https://docs.avaloniaui.net/docs/getting-started/create-app-templates) [S, search snippet only; fetch returned 404]
- Android setup: run `dotnet workload install android` (may need sudo); distribution-packaged .NET may not support workloads (NETSDK1139 error suggests the Microsoft SDK). JDK 11 or later per Avalonia; Android SDK via command-line tools with `ANDROID_HOME` or `AndroidSdkDirectory`. — [Avalonia Android dev environment](https://docs.avaloniaui.net/docs/guides/platforms/android/setting-up-your-developer-environment-for-android) [S]
- Microsoft's `InstallAndroidDependencies` MSBuild target can install the Android SDK and Java SDK (`JavaSdkDirectory` needs an absolute path). Source found only via search snippet; primary page returned 404. — [WebSearch result citing Microsoft Learn](https://learn.microsoft.com/fi-fi/dotnet/android/getting-started/installation/dependencies) [S]
- Desktop SDK: WPF/WinForms (`UseWPF`) can only target Windows; Avalonia heads do not need that. — [MSBuild desktop props](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop) [V]

### Inferences
- Structure proposal: `src/Bazaar.Client.Core` (`net10.0`, Avalonia, no platform code); `src/Bazaar.Client.Linux` (`net10.0`, desktop head); `src/Bazaar.Client.Windows` (`net10.0-windows`, desktop head); `src/Bazaar.Client.Android` (`net10.0-android`). Heads reference Core; Core must not reference any head [U].

### Gaps
- Current `avalonia.xplat` template contents and short names: not verified.
- Minimum Android API level / `SupportedOSPlatformVersion` for `net10.0-android`: not verified (Microsoft Android install page returned 404).
- Size of the Android workload and SDK downloads: not verified; no figure found in sources read.
- Whether the Android workload install works in this container (proxy/network to Google SDK servers): not tested.

---

## 5. What can be built in the Linux container (no install performed)

### Cannot be verified from my sources; labels show confidence
- Avalonia core library (`net10.0`) and Linux desktop head (`net10.0`): should build on Linux x86_64 with the SDK; no Windows or Android dependency [U].
- Headless test project (`Avalonia.Headless.XUnit`, `net10.0`): should build and run with no display, since headless uses its own platform [U, based on section 3 docs].
- Android head (`net10.0-android`): needs the Android workload (`dotnet workload install android`), a JDK (11+ per Avalonia, [S]) and the Android SDK; build may work in container, run and sign do not (no emulator/device) [U].
- Windows head (`net10.0-windows` with `EnableWindowsTargeting=true`): EnableWindowsTargeting is the property used to build Windows TFMs on non-Windows hosts [U, prior knowledge; not found in the pages read]. Running it is not possible in the container; WinUI/WPF runtime needs Windows.
- Windows Service package (`Microsoft.Extensions.Hosting.WindowsServices`): compiles on Linux only if the target is not Windows-only; the MS page uses `net10.0-windows` [V], so keep Host on `net10.0` and check the package for platform attributes [U].
- Nothing can be run here: Windows Service install, Hyper-V, Roblox Studio, Android emulator, and Windows head runtime.

---

## 6. Worker host for Windows Service and systemd (Bazaar.Server.Host)

### Takeaway
One executable with `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.WindowsServices` (Windows Service) and `Microsoft.Extensions.Hosting.Systemd` (systemd). Each one only activates in its own environment.

### Cited Findings
- Windows Service: add `Microsoft.Extensions.Hosting.WindowsServices`; in `Program.cs` call `builder.Services.AddWindowsService(options => { options.ServiceName = "..."; })` with `Host.CreateApplicationBuilder(args)`. Register `AddHostedService<T>()`. The MS sample targets `net10.0-windows` with `Microsoft.Extensions.Hosting` 10.0.12 and `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12. Service created with `sc.exe create "<name>" binpath= "<path>.exe"`; the Microsoft doc says "Installing... .NET SDK also installs the Microsoft.NET.Sdk.Worker and the worker template"; recovery via `sc.exe failure ... actions= restart/60000/...`. — [Worker as Windows Service](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service) [V]
- `BackgroundServiceExceptionBehavior.StopHost` is the default since .NET 6; for restart by SCM, exit with non-zero code (`Environment.Exit(1)`). — [Worker as Windows Service](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service) [V]
- systemd: package `Microsoft.Extensions.Hosting.Systemd`; `dotnet add package Microsoft.Extensions.Hosting.Systemd`; it activates only when running as a systemd service; unit file needs `Type=notify`. API reference names `UseSystemd(this IHostBuilder)`; a Code Maze tutorial uses `AddSystemd()` on `builder.Services` — the two names disagree and were not reconciled. — [Code Maze](https://code-maze.com/aspnetcore-running-applications-as-linux-service/) [S]; [API ref UseSystemd](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.systemdhostbuilderextensions.usesystemd?view=net-9.0-pp) [S]

### Inferences
- Bazaar.Server.Host should use `net10.0` (not `-windows`) and reference both packages; the Windows-only API must sit behind the provider interface per the project rules. Check whether `AddWindowsService` is a no-op on Linux [U: MS page does not state this].
- Verify the systemd call name (`AddSystemd` vs `UseSystemd`) against the installed package before coding.

### Gaps
- The Microsoft systemd page URL 404'd; the systemd API and package version for .NET 10 not verified from a primary source.
- `Environment.Exit` approach in a platform-neutral host: not verified for Linux.

---

## 7. Directory layout, build scripts

### Proposed layout [U]
```
Bazaar/
  global.json              (sdk pin + test runner)
  Directory.Build.props
  Directory.Packages.props (CPM)
  Bazaar.slnx
  .editorconfig
  build.sh                 (Linux/container)
  build.ps1                (Windows)
  src/
    Bazaar.Contracts/
    Bazaar.Server/         (platform-neutral library)
    Bazaar.Server.Host/    (Exe; Windows Service + systemd)
    Bazaar.Sdk/
    Bazaar.Client.Core/    (Avalonia library)
    Bazaar.Client.Linux/
    Bazaar.Client.Windows/ (net10.0-windows; Windows only)
    Bazaar.Client.Android/
  tests/
    Bazaar.Contracts.Tests/
    Bazaar.Server.Tests/
    Bazaar.Client.Core.Tests/   (Avalonia.Headless.XUnit)
  docs/
  build/                    (shared scripts, nuget config)
```

### Minimal build.sh idea [U]
```sh
#!/usr/bin/env bash
set -euo pipefail
dotnet --version
dotnet restore Bazaar.slnx
dotnet build Bazaar.slnx -c Release --no-restore -warnaserror
dotnet test  Bazaar.slnx -c Release --no-build
```
Note: `dotnet build` with a `.slnx` and `-warnaserror` [U]; the headed Android and Windows heads may need `-p:EnableWindowsTargeting=true` or workload install per section 5 [U]. Keep platform heads out of the default `build.sh` until the workloads are present.

---

## 8. Analyzers and formatting for AI-written code

### Cited Findings
- .NET SDK analyzers run by default (`AnalysisLevel` default `latest` for .NET 5+). — [MSBuild props](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props) [V]
- `EnforceCodeStyleInBuild` turns code-style rules into build-time checks. — [MSBuild props](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props) [V]
- Meziantou.Analyzer and Roslynator: not researched (no source read). [Gap]

### Proposed
- Start with SDK analyzers + `.editorconfig` rules set to `error` for nullable and async (CA2007 off for apps) [U].
- Add `Meziantou.Analyzer` and `Roslynator.Analyzers` only after checking their current NuGet versions for net10 support [U, not verified].
- `dotnet format` (`dotnet format style --verify-no-changes` and `dotnet format analyzers`) for CI [U; command not verified against docs].

---

## Conflicts found
1. xUnit v3 lines: `xunit.v3.mtp-v2` 4.0.0-pre.108 (xUnit docs) vs `Avalonia.Headless.XUnit` 12.1.4 depending on `xunit.v3.extensibility.core` >= 3.2.2 (NuGet). Must be resolved by restore before scaffolding.
2. systemd API name: `UseSystemd` (API reference) vs `AddSystemd()` (Code Maze). Check package.
3. Windows Service sample uses `net10.0-windows`; Bazaar.Server.Host must stay OS-agnostic (DECIDED). Use `net10.0`; keep Windows packages behind the host provider.

## Gaps (could not verify)
- Microsoft pages for .NET Android install (dependencies, installation) and Avalonia templates page returned 404.
- Exact `global.json` `sdk` block, `EnableWindowsTargeting` text, `TreatWarningsAsErrors` text, `CentralPackageVersionOverrideEnabled`.
- Avalonia `avalonia.xplat` template, Avalonia.Headless.XUnit csproj, `UseSkia` package names.
- Android minimum API, Android workload size, whether the workload installs in this container.
- Meziantou / Roslynator analyzers and current versions.
- Whether `dotnet test` MTP mode works with xUnit v3 4.0 prerelease under SDK 10.0.402 (not run; no install per instructions).
