# 40. Startup performance: ReadyToRun, runtime levers and the client preloader

Status: PROPOSED (owner request, 2026-11-11). Scope: how the Bazaar clients start fast: precompiled code, runtime settings, and a warm background process ("preloader") that makes the app icon open instantly. Extends doc 28 (Windows client, startup budgets) and doc 30 (Linux packaging). Where they disagree, `DECISIONS-LOG.md` wins once the owner accepts this doc.

Research behind it (notes, no code):
- `docs/research/notes/startup-performance/dotnet-runtime-levers.md`
- `docs/research/notes/startup-performance/avalonia-and-prior-art.md`
- `docs/research/notes/startup-performance/repo-r2r-feasibility.md`

## Recommendation in one paragraph

Ship ReadyToRun (R2R) publish for the two GUI heads (Windows and Linux), framework-dependent or self-contained as their packaging already is. Keep the default tiered JIT, with dynamic PGO on. Do not use Native AOT for the main client; use it only for the small launcher stub if it ever needs to be tiny. Build the preloader as a client module: a hidden, warm head process started at login (Windows Run key or logon task, Linux systemd user unit or XDG autostart), opt-in and off by default, with a memory budget. Measure every claim on the owner's PC before the defaults change. No composite R2R, no MIBC profile in the first change.

## Why

- Doc 28's startup budgets (launcher to window 1.0 s warm, dashboard 0.5 s from cache) are PROPOSED and unmeasured. This doc adds the measurement plan that decides them.
- The owner wants clicking the icon to open the app with no visible JIT or first-paint wait. Two levers do that: make the cold path cheaper (R2R, compiled XAML and bindings, fewer startup allocations), and make the cold path rare (a warm hidden process).
- Native AOT is rejected for the main client. It needs trimming-clean code, no reflection-based bindings, and it removes dynamic loading and Reflection.Emit. Avalonia documents it, but with no Windows startup number. The owner has ruled it out; this doc keeps it only as an option for helper binaries.

## Levers, ranked

| # | Lever | Expected effect | Cost | Status in this doc |
|---|---|---|---|---|
| 1 | `PublishReadyToRun` on the Windows and Linux heads, RID-specific, Release only | Less JIT at first call. Largest on the first window. Assemblies grow 2 to 3x | Low: publish-only property; longer publish | **Do now** |
| 2 | Compiled XAML and compiled bindings (`x:DataType`, `x:CompileBindings`) | Less reflection at first paint | Low: already the Avalonia default from 12 | **Verify no reflection bindings in the client** |
| 3 | Tiered compilation with TieredPGO (default on) | Hot code improves over time; startup not changed | None | Keep defaults |
| 4 | Warm hidden process ("preloader") | Icon click shows the window in the time of a Show call | Medium: a second module, idle RAM | **Build as a module, opt-in** |
| 5 | Local snapshot for first paint (doc 27 cache) | Dashboard shows without a server round trip | Already in design | Reuse doc 27 |
| 6 | Server GC or DATAS for the client | Not a startup lever; a client should stay on workstation concurrent GC | n/a | Rejected for the client |
| 7 | Composite R2R (self-contained) | Smaller JIT set across framework and app, longer builds | Medium to high | Measure later, not in first change |
| 8 | MIBC startup profile for crossgen2 (`--mibc`, `--embed-pgo-data`) | Better method order and inlining decisions | Medium: needs a trace workflow and a profile to keep fresh | Measure later; no official startup-profile workflow was found |
| 9 | Native AOT, whole client | Lowest start time on paper | High; Avalonia constraints; owner rejected | **Rejected** |
| 10 | NativeAOT-LLVM | Experimental; WebAssembly-only on the branch checked | High | Not considered |
| 11 | `TieredCompilation` knobs (quick JIT for loops, OSR, call-count thresholds) | Small, unverified | Low, but the values are not confirmed for .NET 11 | Do not tune without a measurement |

Notes on .NET 11 facts (from the runtime notes): R2R x64 targets x86-64-v3 on Windows and Linux with a v2 baseline, so older CPUs may pay extra. The owner's CPUs need a check. Dynamic PGO is on by default since .NET 8 (secondary sources say it is still on in 11). Runtime Async is preview. No .NET 11 change to R2R, OSR, GC defaults or trimming was found in the GA, RC1 or preview notes. Exact `TC_*` names and the .NET 11 partial-R2R SDK property name were not verified and are not used here.

## The R2R change (cheap win, in this PR)

Put the property in the two GUI head projects, not in `Directory.Build.props`, so Core, Launcher, tests and Android are untouched:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release' And '$(RuntimeIdentifier)' != ''">
  <PublishReadyToRun>true</PublishReadyToRun>
</PropertyGroup>
```

`PublishReadyToRun` only has an effect on `dotnet publish` with a RID. Packaging already publishes with `-r <rid> --self-contained true` (release.yml, build-msix.ps1, build-appimage.sh), so no workflow change is needed for the publish path.

Risks the first change must check:
- `TreatWarningsAsErrors` is on. Confirm the publish does not fail on R2R warnings.
- The SDK pins `11.0.100-rc.2`. The crossgen2 pack must match the runtime. The GA pack (11.0.0) exists on NuGet, but the RC pin may pick a different pack. Pin the SDK to the GA release when it ships (CLAUDE.md says the repo pins the latest 11.0 SDK; `global.json` currently pins the RC, a conflict to resolve with the owner).
- Avalonia warns that R2R may change how native `.so` files resolve on Linux. The Linux packaged build needs a smoke run (tarball, AppImage).
- The Flatpak build is offline. Enabling R2R there needs the crossgen2 pack added to the offline source set first.
- CI runs only `build.sh`/`build.ps1`, which never publish. A publish smoke job for both OSes is needed so an R2R break is seen in a PR, not at tag time.

## The preloader (client module)

Goal: clicking the app icon always opens the window immediately. Mechanism:

1. **Autostart at login, opt-in.** Windows: a per-user Run key or a logon Task Scheduler task (the MSIX `windows.startupTask` is the alternative once packaging is settled; it decides the mechanism, an open question). Linux: a systemd user unit (`bazaar-preloader.service`, enabled per user; linger only if the owner wants it at boot) with an XDG autostart file as the fallback for other desktops.
2. **Warm hidden process.** The preloader starts the client head with `--preload`: the runtime, Avalonia and the R2R code are loaded, the cached shell is built, and the window is created but not shown. The icon launches a second instance, which the existing `LaunchPipe` single-instance logic forwards to the warm process, which shows its window. If the warm process is missing, the icon starts a normal cold instance (the fallback is always the cold path; nothing depends on the preloader).
3. **Memory budget.** A setting (`client.preload.max_mb`) caps the warm process's working set. If idle RAM is below the budget, the preloader exits. Nothing is preloaded on battery saver or under memory pressure.
4. **Off by default.** The owner turns it on in settings. The first-run prompt explains what stays in memory. The setting is audited like other settings.
5. **Android.** No warm hidden process. Background starts and cached-process kills make it unreliable. Warm-up comes from cold-start work and the content-free push wake-ups already in CLAUDE.md.

The module lives in `Bazaar.Client.Core` behind a `IPreloadProvider` interface, with `Bazaar.Client.Windows` and `Bazaar.Client.Linux` supplying the autostart implementation. It is a separate module, so it can be disabled without touching the client.

## Measurement plan (decides the budgets)

The owner's PC (`CKorvix_V1`) is the baseline machine. Work happens in a worktree under `V:\git\Bazaar.Workspace`, never the shared checkout.

For each of win-x64 and linux-x64, publish framework-dependent and R2R variants of the same commit, then record, over at least 10 cold and 10 warm launches each:
- process start to first window shown (cold), and warm-preload-to-visible (warm);
- time to first Constellation or dashboard frame from the local cache;
- JIT methods compiled at startup (event counters, `DOTNET_ReadyToRun=0` A/B);
- publish size and publish time, R2R on and off;
- peak working set of the preloaded idle process.

Results go in a follow-up note in `docs/research/notes/startup-performance/`. A budget in doc 28 changes only from those numbers.

## Open questions

1. Windows packaging: MSIX or unpackaged? It decides between a `windows.startupTask` and a Run key.
2. SDK pin: move `global.json` from the RC to the GA 11.0 SDK when it ships? (Owner decision; `global.json` and CLAUDE.md currently disagree.)
3. Preloader default: stay off (this doc's recommendation) or on for Windows?
4. Memory budget default for the warm process (this doc proposes a setting, no value until measured).
5. Composite R2R and MIBC profile: measure in a second round or not at all?
