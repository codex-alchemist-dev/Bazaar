# .NET 11 runtime startup levers for a large Avalonia desktop client

Status: research notes, not a design decision. Date: 2026-11-11.
Scope: runtime and SDK levers only (ReadyToRun, PGO, tiering, GC, trimming, Native AOT facts). Avalonia-specific startup work is in `avalonia-and-prior-art.md` in this folder.
Target: .NET 11 (GA index entry dated 2026/11/10, see S7). .NET 10 is the LTS fallback.

Labels used below: **[V]** verified (primary source read in this session), **[S]** secondary (blog, search snippet, or third-party), **[U]** unverified (could not check). Source IDs are in the list at the end.

Caveat on measurement: no number in this file is a measured startup gain for Bazaar. Every "expected effect" is a documented direction, not a benchmark. The Bazaar client needs its own cold-start measurement (see open facts).

## 1. Lever table

| Lever | What it does | Expected startup effect | Cost | Platform support | .NET 11 status | Source |
|---|---|---|---|---|---|---|
| ReadyToRun (`PublishReadyToRun=true` with a RID) | Precompiles IL to native code at publish; the JIT still runs for what R2R cannot precompile. | Largest documented win for "large amounts of code"; fewer methods JIT'd at load. [V] | Assemblies grow 2-3x; RID-specific publish; slower publish. | Win x64/Arm64, Linux x64/Arm64, macOS; cross-compile table in S1. | Available. R2R x64 target raised to x86-64-v3 on Windows/Linux (see last row). | S1, S13 |
| R2R of framework (framework-dependent vs self-contained) | Framework assemblies are already R2R in the shipped runtime. Self-contained publish also precompiles the framework that ships with the app. | Framework code is already precompiled in framework-dependent apps; self-contained adds app-shipped framework to the R2R set. [V] | Self-contained: larger output, ships its own runtime copy. | Both. | Available. | S1 |
| Composite R2R (`PublishReadyToRunComposite=true`) | Compiles a set of assemblies together so the compiler can inline across them and compile more methods ahead of time. | Potentially more precompiled methods than per-assembly R2R. [V] | Much longer compile; app "significantly larger"; self-contained only (ignored otherwise); assemblies must ship together. | Self-contained only. | Available since .NET 6; enabled by default for self-contained single-file + R2R since .NET 7. | S1 |
| Partial R2R (`crossgen2 --partial`) | Compiles only the methods a profile (MIBC) or method list names, leaving the rest to the JIT. | Smaller image than full R2R; startup effect depends on which methods are listed. [S] | Needs a profile or method list to be useful. | crossgen2. | Option exists in crossgen2 (`--partial`). New .NET 11 SDK property for "partial R2R for upstack tooling" exists but its name was not captured. | S22, S12, S28 |
| crossgen2 `--mibc` (static PGO for R2R) | Feeds a `.mibc` profile to crossgen2 so R2R code is laid out and optimized for observed hot paths. | Unmeasured for Bazaar. The runtime team uses it for framework R2R images. [S] | Need a representative trace or a method list. | crossgen2. | Option exists (`--mibc`, `-m`). | S22, S21, S14 |
| Profile collection and conversion (`dotnet-trace` + `dotnet-pgo`) | `dotnet trace collect` with runtime JIT/R2R keywords, then `dotnet-pgo` turns the trace into `.mibc` (for crossgen2) or `.jittrace`. | Enables the MIBC lever above. | Needs a scripted training run of the client; trace must include MethodDetails events. | Windows (PerfView or dotnet-trace), any. | Tool README in the dotnet/runtime tree; README is not dated, so treat as current-unverified. | S21 |
| Startup profile / Multicore JIT (`ProfileOptimization`, `AssemblyLoadContext.SetProfileOptimizationRoot`) | Records which methods run at startup and JITs them on background threads on later runs. | Targets JIT time on multi-core machines; ProfileOptimization API no-op on single core. [S] | Profile file to manage; a call before the first `StartProfile`. | Windows and Linux desktop; ASP.NET enables it automatically. [S] | Still present. .NET 11 preview 1 raised the Multicore JIT `MAX_METHODS` limit. | S26, S9 |
| Tiered compilation (`TieredCompilation`, default on) | Tier 0 quick JIT, then rejit hot methods optimized in the background. | Keeps tier 0 cheap at startup; "by delaying until after 'startup' the Tier0 code is left running longer." [V] | Disabling it removes dynamic PGO and forces full-opt JIT or R2R code only. | All. | Default on since .NET Core 3.0. | S2, S16, S15 |
| QuickJit (`TC_QuickJit`) | Quick JIT for methods without precompiled code. | Faster JIT at startup; slower steady-state code. [V] | Lower code quality at tier 0 until tier 1. | All. | Default on since .NET Core 3.0. | S2 |
| QuickJitForLoops (`TC_QuickJitForLoops`) | Applies quick JIT to methods with loops. | Fewer full-opt compiles at startup. | Long loops can stay in tier 0 unless OSR is active. | x64/Arm64 (OSR). | **Conflict**: S2 says "If you omit this setting, quick JIT is not used for methods that contain loops." S18 (PR #65675, merged 2022 for .NET 7) flipped the default on for x64/arm64. Current effective default not verified. | S2, S18, S17 |
| On-stack replacement (OSR) | Moves a running tier 0 frame to optimized code at a loop patchpoint. | Makes QuickJitForLoops safe for long loops. | Patchpoint counters in tier 0 code. | x64/Arm64. | Present. Design doc gives `OSR_HitLimit` default 0x10 and `TC_OnStackReplacement_InitialCounter` default 1000; name may have changed (doc mentions `OSR_HitCount`). | S17, S18 |
| Tier call-count threshold and delay (`TC_CallCountThreshold` 30, `TC_CallCountingDelayMs` 100) | Method is promoted after 30 calls; call counting starts after a 100 ms startup delay that resets on any tier 0 JIT. | Raising the delay keeps more code at tier 0 during startup. | Lower steady-state speed during the delay window. | All. | Values from design doc; env names as given in the task but the CLR config header could not be read. | S16, [U] names |
| `TC_BackgroundWorkerTimeoutMs` | Background tier-1 compilation runs on one thread-pool thread in chunks of up to 10 ms, then requeues. | Affects when tier 1 work completes, not startup directly. | Unknown. | All. | Behavior described in design doc; exact knob default not verified. | S16, [U] |
| TieredPGO (`DOTNET_TieredPGO`, MSBuild `TieredPGO`) | Dynamic PGO: tier 0 instrumentation, then tier 1 uses observed types and paths. | Helps steady state. Startup cost is the instrumentation in tier 0 for short runs. [S] | Extra tier 0 instrumentation; R2R methods are recompiled at tier 1 instead of kept. | All. | Enabled by default in .NET 8 [V]. Not restated for .NET 9-11 in primary docs; S2 gives no default. | S14, S15, S2, S27 |
| Server vs Workstation GC (`System.GC.Server`) | Server GC uses per-core heaps and threads. | Not a documented startup lever; Server GC adds heaps and threads at init. | Higher memory and thread use; desktop clients usually keep Workstation. | All. | Default Workstation. [V] | S3 |
| DATAS (`System.GC.DynamicAdaptationMode`) | Adapts heap count and gen0 budget to app size; applies only to Server GC. | Not a startup lever. | Server GC only. | Server GC. | Enabled by default since .NET 9 (Server GC only). | S3 |
| Concurrent (background) GC (`System.GC.Concurrent`) | Background gen2 collection. | Not a startup lever; default true. | None to keep. | All. | Default true. [V] | S3, S19 |
| `GCHeapHardLimit` / `GCHeapHardLimitPercent` | Caps GC committed heap. | None for startup; a hard cap can cause OOM. | Risk of OOM in heavy UI workloads. | 64-bit only. | Present. Unset in a memory-constrained container gets a default (20 MB or 75%). | S3 |
| `GCgen0size` (`DOTNET_GCgen0size`, hex) | Sets the smallest gen0 budget. | Could reduce early GCs during startup allocations; unmeasured. [U] | Larger gen0 costs memory. | Unknown in regions GC. | Config exists in gcconfig with no public runtimeconfig name; not in the official GC page. Behavior under regions not verified. | S19, S3 |
| GC config from env vs runtimeconfig | GC settings are read only at GC init. | Env vars must be in place before process start (launcher or shortcut). | Operational. | All. | Documented. | S3 |
| IL trimming (`PublishTrimmed`) | Removes unused framework and app code; turns off trim-incompatible features and shows trim warnings. | Smaller files, fewer types to load; not a direct speedup claim. | Trim warnings must be fixed; reflection-heavy UI needs roots. | Self-contained publish only. | Supported. Trim warnings should be zero per docs. | S4, S5 |
| Trim-analysis roots for Avalonia | Keep reflection-bound types (XAML, DI) via `TrimmerRootAssembly` or `DynamicallyAccessedMembers`. | Prevents runtime breakage after trim. | Developer effort; cascade risk if `All` is used. | Any trimmed build. | Avalonia's own guidance says to add trimmer roots for reflection errors. | S24, S5 |
| Native AOT (`PublishAot`) | Compiles to native ahead of time; no JIT at runtime. | Documented as "faster startup time and smaller memory footprints". Owner has rejected it for the main client; facts only. | No dynamic loading, no `Reflection.Emit`, Windows no built-in COM; requires trimming; Avalonia needs `IsAotCompatible`, `BuiltInComInteropSupport=false` before Avalonia 12. | Win x64/Arm64/x86, Linux x64/Arm64/Arm, macOS (.NET 9+). | Available. .NET 11 added NativeAOT faster interface dispatch and Thread.Interrupt on Windows. | S23, S24, S9, S10 |
| NativeAOT-LLVM | LLVM backend for NativeAOT. | Not applicable to Windows desktop per current sources. | Experimental. | Only WebAssembly is supported on the branch. | Not in .NET 11 release notes; branch README is the only status source. | S25 |
| Runtime Async (`<Features>runtime-async=on</Features>`) | Runtime-managed async state machines; cleaner stacks, lower overhead. | Not a documented startup lever; R2R and NativeAOT support is listed. | Preview feature; opt-in per project. | All. | Preview in .NET 11 (no EnablePreviewFeatures needed for net11.0). The libraries are compiled with it. | S10 |
| R2R default comparers (`Comparer<T>.Default`, `EqualityComparer<T>.Default`) | R2R image now includes a specialized helper that previously fell back to JIT. | Stated as "up to a 20x improvement" for collection operations that use the default comparer; not a startup number. | None. | R2R. | .NET 11 change. | S10 |
| Minimum CPU baseline and R2R target | .NET 11 x86-64 baseline is v2; R2R target for Windows/Linux x64 is v3 (AVX, AVX2, BMI1/2, FMA, LZCNT, MOVBE, F16C). | On CPUs without v3, R2R images may add startup overhead ("might be additional startup overhead"). | Check the owner's target CPUs. | Windows/Linux x64. | .NET 11 breaking change. | S13, S10 |

## 2. Key facts behind the table

- **R2R does not remove the JIT.** The JIT is still needed for generic types defined in other assemblies, interop with native code, hardware intrinsics the compiler cannot prove safe, certain unusual IL, and dynamic method creation [V, S1]. R2R code still needs the type loader to create types [V, S20].
- **Tiering replaces hot R2R code.** "Tiered compilation will replace commonly used ReadyToRun methods with JIT-generated methods" [V, S1]. For methods precompiled in R2R images, the precompiled code is the tier 0 version [V, S16].
- **R2R on the framework.** "The SDK will precompile the assemblies that are distributed with the application. For self-contained applications, this set of assemblies will include the framework." [V, S1]. The runtime libraries are already R2R [V, S1].
- **Size.** "In general, the size of an assembly will grow to between two to three times larger." [V, S1]
- **Composite restrictions.** Self-contained only; "significantly longer" compile; assemblies "must be distributed together" [V, S1].
- **Symbols for profiling.** `PublishReadyToRunEmitSymbols` writes `.ni.pdb` (Windows) or `.r2rmap` (Linux) [V, S1].
- **Trimming.** "Trimming is *only* available to applications that are published self-contained." [V, S4]. "An app that uses trimming shouldn't produce any trim warnings." [V, S5]. IL2026 means `RequiresUnreferencedCode`; IL2070 means a `DynamicallyAccessedMembers` requirement is not satisfied [V, S5].
- **GC settings timing.** "These configurations are only read by the runtime when the GC is initialized" [V, S3].
- **dotnet-pgo.** Collect with `dotnet trace collect ... --providers Microsoft-Windows-DotNETRuntime:0x6000080018:5`; produce `.mibc` for crossgen2 `--mibc` or `.jittrace` [V, S21]. The README says the tool requires MethodDetails events [V, S21].
- **.NET 11 release notes.** The GA release-notes index and the runtime, preview-1 and RC-1 runtime pages were keyword-searched for ReadyToRun, R2R, crossgen, startup, TieredPGO, OSR, GC, trimming, MIBC, composite, and startup profile [V, S6-S9]. Only the items listed in the table turned up. No .NET 11 R2R, PGO, GC-default, or trim change was found in the release notes [V, S6-S9]. The .NET 11 runtime overview page contains the R2R and Runtime Async items above [V, S10].

## 3. Newly shipped or previewed for .NET 11 in these areas

- **Shipped (per primary runtime page):** R2R x64 target raised to x86-64-v3 on Windows/Linux, baseline to v2 [V, S13, S10]. Runtime Async with R2R and NativeAOT support (preview) [V, S10]. `Comparer<T>.Default` specialized in R2R images [V, S10]. NativeAOT faster interface dispatch [V, S10]. Multicore JIT `MAX_METHODS` raised [V, S9]. In-process crash report logging [V, S10]. GC fixes for multiple CPU groups on Windows [V, S10].
- **SDK (primary):** Partial R2R property for upstack tooling (MAUI/macOS), excluded from the composite image [V, S12]. Name not captured [U]. The `dotnet` CLI itself uses NativeAOT by default in .NET 11, with managed fallback [V, S12]. Precedent only.
- **Not found in .NET 11 notes:** new PGO defaults, OSR changes, GC default changes, trimming analyzer changes beyond SDK analyzers [V, S6-S9, S12].
- **dotnet/runtime issues:** a search for .NET 11 R2R startup regressions found only Android/MAUI secondary reports; no GitHub issue was read [U, S-search].

## 4. Conflicts found

1. **Windows Arm64 baseline.** The runtime page says the Windows JIT/AOT minimum is updated to armv8.0-a + LSE [S10]. The breaking-change page says "For Windows, there's no change to the minimum hardware" [S13]. The breaking-change page was updated 2026-08-19 and is the more specific source, but the owner should check. Not relevant to x64 Windows.
2. **QuickJitForLoops default.** S2 says quick JIT for loops is off when the setting is omitted. S18 and S17 describe it as on for x64/arm64 since .NET 7. The current effective default is unverified.
3. **Dynamic PGO on by default.** Primary source confirms .NET 8 [V, S14]. Secondary sources say it is still on in .NET 11 [S27]. Primary docs for .NET 9-11 do not restate the default [V, S2, S15]. Open.

No conflict was found with the DECIDED items in CLAUDE.md. Note: self-contained publish (needed for trimming and composite) ships its own runtime copy. This sits next to the "no older-runtime pins" rule; the choice of framework-dependent vs self-contained is an owner decision and is not made here.

## 5. Open facts (could not verify)

- Current `clrconfignames.h` and `gcconfig.h` for TC_* defaults beyond the design doc, `TC_BackgroundWorkerTimeoutMs` default, `TieredPGO` runtimeconfig name, `DOTNET_ReadyToRun` runtimeconfig name. Fetch of the CLR config header failed (404).
- crossgen2 help text for `--partial`, `--composite`, `--embed-pgo-data`, `--opt-cross-module`; only option names were verified from source [V, S22]. Semantics of `--partial` are from a crossgen2 source comment and a MAUI PR snippet [S, S28].
- Official "startup profile" workflow: no official Microsoft doc was found; the MIBC/dotnet-pgo path is documented only in the tool README [V, S21] and third-party posts.
- Name of the .NET 11 SDK partial-R2R MSBuild property [U].
- Whether `GCgen0size` is honored under the regions GC in .NET 11, and its effect on startup [U].
- Whether `TC_CallCountThreshold`, `TC_CallCountingDelayMs` and `TC_BackgroundWorkerTimeoutMs` env names are current [U].
- Avalonia's current trimming warning profile and whether Avalonia is trim-clean on .NET 11 [U]. Avalonia's own docs give AOT setup, not a trim-warning list [S24].
- .NET 11 default for TieredPGO and QuickJitForLoops in the shipped runtime [U].
- Any measured startup numbers for .NET 11 vs .NET 10 on Windows. Search results were secondary blogs with unverified numbers; none were used [S-search, U].
- Android R2R/AOT behavior for Avalonia: not researched.
- Whether the Bazaar target CPUs meet x86-64-v3 (AVX2, BMI2, FMA, etc.) [U]. Needs the NUC and client hardware list.
- Whether GitHub issues in dotnet/runtime show a .NET 11 R2R regression [U].
- dotnet-pgo README date and currency vs .NET 11 [U].
- Startup numbers for R2R, composite, MIBC, PGO on Bazaar: not measured. Measure cold start on the Windows client (Stopwatch around `Program.Main`, plus ETW/`dotnet-counters` for JIT time) before any lever is adopted.

## Sources

- S1 https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run (read)
- S2 https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation (read)
- S3 https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector (read)
- S4 https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trim-self-contained (read)
- S5 https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/fixing-warnings (read)
- S6 https://github.com/dotnet/core/tree/main/release-notes/11.0 (read)
- S7 https://github.com/dotnet/core/blob/main/release-notes/11.0/11.0.0/11.0.0.md (keyword-searched)
- S8 https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/rc1/runtime.md (keyword-searched)
- S9 https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview1/runtime.md (keyword-searched)
- S10 https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime (read; RC 2 version)
- S11 https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview (read)
- S12 https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/sdk (read; RC 2 version)
- S13 https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/11/minimum-hardware-requirements (read)
- S14 https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-8/ (read, partial)
- S15 https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/ (read, partial)
- S16 https://github.com/dotnet/runtime/blob/main/docs/design/features/tiered-compilation.md (read; design doc, may be stale)
- S17 https://github.com/dotnet/runtime/blob/main/docs/design/features/OsrDetailsAndDebugging.md (read; design doc, may be stale)
- S18 https://github.com/dotnet/runtime/pull/65675 (read)
- S19 https://github.com/dotnet/runtime/blob/main/src/coreclr/gc/gcconfig.h (read)
- S20 https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-overview.md (read)
- S21 https://github.com/dotnet/runtime/blob/main/src/coreclr/tools/dotnet-pgo/README.md (read)
- S22 https://source.dot.net/crossgen2/Crossgen2RootCommand.cs.html (read; option names only)
- S23 https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ (read)
- S24 https://docs.avaloniaui.net/docs/deployment/native-aot (read)
- S25 https://github.com/dotnet/runtimelab/tree/feature/NativeAOT-LLVM (read)
- S26 https://learn.microsoft.com/en-us/dotnet/api/system.runtime.profileoptimization (search snippet only; not fetched)
- S27 https://startdebugging.net/2026/07/what-is-pgo-in-dotnet-and-do-i-need-to-opt-in/ (search snippet, secondary)
- S28 https://github.com/dotnet/maui/pull/34660 (search snippet, secondary)
- S29 https://startdebugging.net/2026/05/native-aot-vs-readytorun-vs-jit-in-dotnet-11/ (search snippet, secondary)
- S-search: web search results for Multicore JIT, NativeAOT-LLVM, .NET 11 R2R regression, and .NET 11 vs 10 benchmarks (secondary; no numbers used)
