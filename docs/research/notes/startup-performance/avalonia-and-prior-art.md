# Startup performance: Avalonia cold start and warm-process prior art

Research date: 2026-11-11. Status: RESEARCHED (notes only; not a design decision). No DECIDED item is changed by this note.

Labels used on every claim:
- **verified**: the primary source was read in this session (URL given).
- **secondary**: vendor news, third-party guide, mirror, or a search-result snippet that was not opened as a primary page.
- **unverified**: not confirmed in this session (from memory, inference, or a failed fetch).

Failed or partial fetches are listed in section 7.

---

## 1. Avalonia: trimming, Native AOT, compiled bindings, XAML compilation

| Claim | Label | Source |
|---|---|---|
| Native AOT: `PublishAot` only on the main exe; `IsAotCompatible` on all projects; `BuiltInComInteropSupport=false` "Necessary before Avalonia 12.0". | verified | https://docs.avaloniaui.net/docs/deployment/native-aot |
| Native AOT page: "When using Native AOT, XAML is compiled into the application at build time." Lists "Faster application startup time" as the first benefit. | verified | same |
| Native AOT limits: dynamic control creation needs trimmer configuration; some third-party controls are not AOT-compatible; advises `x:CompileBindings="True"` and no dynamic XAML loading at runtime. | verified | same |
| Avalonia compiles `.axaml` to IL during the build (XamlX); generated IL builds the visual tree "without runtime XML parsing". | verified | https://docs.avaloniaui.net/docs/xaml/compilation |
| Compiled-XAML page claims "faster startup, smaller binaries, and compile-time error detection" with no measurements. | verified (claim is unmeasured) | same |
| `x:DataType` per control with `x:CompileBindings`; project-wide via `AvaloniaUseCompiledBindingsByDefault` every binding needs `x:DataType`. For Native AOT compiled bindings are "required". | verified | same |
| Compiled bindings "resolve property paths at compile time, avoiding runtime reflection." | verified | https://docs.avaloniaui.net/docs/app-development/performance |
| Avalonia 12: compiled bindings "now `true` by default"; "Any `Binding` usage in XAML code now maps to `CompiledBinding`". The page says nothing on trimming, AOT, R2R or startup. | verified | https://docs.avaloniaui.net/docs/avalonia12-breaking-changes |
| Avalonia 12 announcement (vendor): Android startup "1,960ms to 460ms with NativeAOT, a 4x improvement"; no device, OS version, app or method given. | verified text; **vendor claim, unreproduced** | https://avaloniaui.net/blog/avalonia-12 |
| Avalonia 11 launch coverage: AOT and trimming "full support", about 60% smaller apps. | secondary | https://avaloniaui.net/Blog/welcome-to-the-new-era-of-app-development-introducing-avalonia-v11 (snippet only) |
| Avalonia 12 Native AOT/trimming page: not found; whether the `BuiltInComInteropSupport` note still applies to Avalonia 12 is unknown. | unverified | (v12 page not located) |

Startup-path levers named by Avalonia itself (XPF performance page):
- ReadyToRun: "can reduce application startup time substantially, particularly on Linux embedded devices". Set `PublishReadyToRun` and publish with a RID. Per the page, "WPF libraries shipped by Microsoft are normally pre-compiled as R2R, but XPF libraries are not by default." Source: https://docs.avaloniaui.net/xpf/configuration/performance (verified).
- BAML: "BAML (compiled XAML) provides the best loading performance" for dynamic XAML. There is "no supported public API for creating loose BAML files". Same source (verified).
- Skia knobs: `SkiaOptions.MaxGpuResourceSizeBytes`, `CompositionOptions.UseRegionDirtyRectClipping`. No startup-specific advice. Same source (verified).
- The page does not cover trimming, fonts, or environment variables. Same source (verified).

Startup hot spots:
- No official Avalonia list of startup hot spots was found. The `docs.avaloniaui.net/docs/deployment/startup-performance` path returned 404 (see section 7).
- Third-party checklist (not Avalonia-official): "Preload only resources needed at first screen"; "Avoid loading large resource dictionaries at startup if not needed"; "Verify startup time, first interaction latency". Source: https://wieslawsoltes.github.io/development-plugin-for-avalonia/references/08-performance-checklist (secondary).
- Missing `AvaloniaResource` or `x:Class` produces a load exception with precompiled XAML. Source: community troubleshooting snippet (secondary); the compilation page does not mention `AvaloniaResource` (verified absence on that page).
- Font and Skia init: Skia's first default font-manager creation "will take noticeable time" (Flutter engine change, secondary); fontconfig matching is slow with many fonts on Linux (osdev forum and WebKit reports, secondary). **Not confirmed for Avalonia.** Treat as hypotheses to measure.
- A hello-world Avalonia app at 2.4 s dropping to 1.4 s with R2R: secondary, from an old GitHub issue mirrored on lightrun.com. Re-measure on the current version.

## 2. .NET runtime levers (apply to any Avalonia client)

- Native AOT: "faster startup time and smaller memory footprints"; limits include no `Reflection.Emit`, no dynamic loading, requires trimming, "Windows: No built-in COM"; `System.Linq.Expressions` interpreted. .NET 9+ Windows targets include x64, Arm64 and x86. Source: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ (verified; page updated 2026-11-03).
- ReadyToRun: reduces JIT work at load; assemblies "two to three times larger"; tiered compilation replaces hot R2R methods; needs a RID publish. Source: https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run (verified).

## 3. Prior art: warm or hidden processes (desktop)

| Product | Mechanism | Hidden warm process? | User control and default | Cost evidence | Label |
|---|---|---|---|---|---|
| Edge Startup Boost | "starts core Microsoft Edge processes in the background to keep them ready when the browser is closed"; "minimal processes". | Yes, after the browser is closed. | Toggle at `edge://settings/system/manageSystem`. Support page does not state the default. Policy name `StartupBoostEnabled` under `HKLM\SOFTWARE\Policies\Microsoft\Edge` (per pureinfotech). | Microsoft: "limited impact on device resource usage"; "does not add any additional resources when browser windows are already open". A "about 600 MB" saving is a guide's claim, unverified. Default-off report (WinCentral) is secondary and conflicts with a Cloudinfra post. | MS page: verified (https://support.microsoft.com/en-us/edge/get-help-with-startup-boost). Policy and 600 MB: secondary. |
| Chrome "Continue running background apps when Google Chrome is closed" | Background mode for apps and extensions after the last window closes. | Partly (apps and extensions, not a warm UI). | Sources disagree: some still show the toggle, others say newer builds moved this to per-PWA permissions. Policy exists (CIS benchmark, secondary). | Not found. | **unverified**; no Google primary found. |
| Firefox | No verified "keep running" setting. Windows background updates run a Firefox instance in background mode when Firefox is not running; per the secondary summary, the task exits without work if Firefox is already running. | No warm UI found. | Option under General > Firefox Updates. | None. | Secondary (search snippet). Mozilla support page did not render. |
| Microsoft Teams | Closing the window keeps the app in the tray; option "On close, keep the application running and minimize it to the taskbar". Teams adds itself to startup. | Yes (tray), option-controlled. | Settings > General; startup via Settings > Apps > Startup. | Guides say memory and CPU cost; no numbers. | Secondary (pureinfotech and search summaries). No Microsoft primary. |
| Microsoft Office Startup Boost (Word; TechSpot reports Excel and PowerPoint too, scope disputed) | Two scheduled tasks ("Office Startup Boost", "Office Startup Boost Logon") preload parts of apps into memory, paused, after login. | Preloaded but not visible. | Word > Options > General; Group Policy. Office updates may re-enable it. Gating per MC1041470: at least 8 GB free RAM, 5 GB free disk, 10-minute idle wait after login, only if Word was launched recently, off under Energy Saver. | Message Center text, not a benchmark. | Secondary (https://mc.merill.net/message/MC1041470; search summaries). |

Windows Startup impact ratings (Task Manager): "Low Impact" below 300 ms CPU and 292 KB disk; "Medium" below 1 s or 3 MB; "High" above 1 s or 3 MB. Source: https://support.microsoft.com/en-us/windows/configure-startup-applications-in-windows-115a420a-0bff-4a6f-90e0-1934c844e473 (verified).

Pattern across the prior art: (a) keep a light, non-UI process or pre-loaded state after login or close; (b) make it a visible, user-changeable setting; (c) gate it on idle time and RAM; (d) vendors disagree on defaults. None of the sources read gives an independent benchmark of idle cost.

## 4. Login autostart mechanisms (Windows, Linux)

| Mechanism | Cost | User visibility | Label |
|---|---|---|---|
| HKCU `...\CurrentVersion\Run` value | Runs at every logon; timing not guaranteed: "the system may choose to delay the execution of programs in the Run key and in the Startup group". Command line max 260 chars. | Settings > Apps > Startup shows the entry (per secondary sources), with on/off state stored in `Explorer\StartupApproved\Run` (secondary; not verified against a Microsoft primary). | Run-key facts: verified (https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys). Settings listing and StartupApproved: secondary. |
| Startup folder (`shell:startup`) | Same as Run. | Shown in Startup settings (secondary). | secondary |
| Task Scheduler, "When a user logs on" trigger | Full task scheduling; whether it needs elevation: unverified. | Whether a task created by an app appears in Settings > Apps > Startup: **unverified**. | Trigger list: verified (https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-start-page). Other points: unverified. |
| MSIX / Desktop Bridge `windows.startupTask` | Declared in the package manifest (`desktop:Extension` / `uap5:Extension` with Category `windows.startupTask`). `Enabled="true"` allowed for packaged desktop apps. Desktop apps: `RequestEnableAsync` shows no consent dialog. The user must launch once or enable in Settings > Startup. `DisabledByUser` cannot be overridden by the app. `DisabledByPolicy` is a separate state. | Yes: Settings > Apps > Startup toggle, and Task Manager Startup tab. | verified (https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask). Requires packaging (open decision). |
| Settings > Apps > Startup (UI) | Each app has an On/Off toggle; advanced settings per app. | This is the user's control surface. | verified (support.microsoft.com link above). |
| XDG autostart (`$XDG_CONFIG_HOME/autostart/*.desktop`, `Hidden` key) | Starts with the desktop session. | Desktop environments list them. | **unverified**: the spec fetch returned empty content after a 301 redirect. Known from background knowledge only. |
| systemd `--user` units | Per-user service manager. `user@UID.service` is started by PID 1. `loginctl enable-linger`: "If enabled for a specific user, a user manager is spawned for the user at boot and kept around after logouts"; "allows users who are not logged in to run long-running services". | Less visible than XDG autostart. | Linger text: verified (https://www.freedesktop.org/software/systemd/man/latest/loginctl.html). `user@` start: verified (https://www.freedesktop.org/software/systemd/man/latest/user@.service.html). That the manager stops at logout without linger is an **inference**. `WantedBy=default.target` and graphical-session env setup: **unverified** (the fetched page had only `multi-user.target`). |

## 5. Android: constraints on background warm-up

Verified facts:
- Cold, warm, hot start definitions; "We recommend that you always optimize based on an assumption of a cold start." Source: https://developer.android.com/topic/performance/vitals/launch-time (verified). TTID comes from the system `Displayed` log line; TTFD needs `reportFullyDrawn`.
- Background foreground-service start: apps targeting Android 12 (API 31)+ "can't start foreground services while the app is running in the background, except for a few special cases". Failure throws `ForegroundServiceStartNotAllowedException`. Source: https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start (verified).
- Exemptions include: transition from a user-visible state; user action on a notification, widget or activity; FCM high-priority message (the system can downgrade it); user turns off battery optimizations for the app; `SYSTEM_ALERT_WINDOW` (on Android 15+, with a visible overlay); exact alarm for a user request; current input method; certain boot receivers (with Android 15 type limits). Same source (verified).
- Background activity starts: allowed only with a visible window, a system-sent PendingIntent, `SYSTEM_ALERT_WINDOW`, `START_ACTIVITIES_FROM_BACKGROUND`, etc. Creator and sender opt-in rules from Android 14/15. Source: https://developer.android.com/guide/components/activities/background-starts (verified).
- Cached processes: "the system is free to kill it as needed"; "apps should cease all work while in the cached state"; "Starting in Android 13, an app process may receive limited or no execution time" when cached. Service processes running 30+ minutes "might be demoted". Source: https://developer.android.com/guide/components/activities/process-lifecycle (verified).
- Android 15 limit on `dataSync` and `mediaProcessing` foreground services (6 h in 24 h): secondary (search summary of developer.android.com pages; not opened). Unverified at primary.

Feasibility conclusion: a hidden, always-warm UI process is **not** a supported pattern on Android. A backgrounded app cannot start a foreground service on its own, and a cached process can be killed or starved. The narrow routes are user-initiated (notification, widget, activity) or the FCM high-priority exemption; in both cases the foreground service needs its persistent notification, so the process is user-visible and not hidden. For Bazaar this means the content-free push wake-up design already in CLAUDE.md is the warm path, not a resident process. Google Play policy on these uses: **unverified**.

## 6. Recommendations for a Bazaar Avalonia client

"Measure first" means no change ships until a benchmark on NUC-class hardware (Windows) and a reference Android device exists.

| # | Recommendation | Cost | Risk | Evidence |
|---|---|---|---|---|
| R1 | Cold start is the target on every client. Keep compiled XAML (default), `x:DataType` plus `x:CompileBindings` on every binding (default in Avalonia 12), and a CI first-frame timer. | Low: build settings and one benchmark job. | Low. | Avalonia docs (verified). |
| R2 | ReadyToRun for Windows and Linux publishes (`PublishReadyToRun`, RID publish). Measure before adopting. | Low to moderate: larger files and publish time. | Low to moderate: R2R larger working set; tiering. | Avalonia XPF page and Microsoft R2R page (verified). Gain on Bazaar: unmeasured. |
| R3 | First paint from a local, read-only snapshot of the last dashboard, then refresh from the server. Clients render; the server computes. Stale data is labelled. | Moderate: local cache schema and invalidation. | Stale data; personal fields need encryption at rest (per DECISIONS-LOG, as summarised in CLAUDE.md). | Design constraint from CLAUDE.md; no external benchmark. |
| R4 | Defer heavy startup work: fonts beyond the default set, large resource dictionaries, Monaco, terminal, Constellation. Load on first use. | Low to moderate. | Low; first use of a view is slower. | Third-party checklist (secondary); Skia font hypothesis (secondary). Measure first. |
| R5 | Windows Native AOT spike for the client, after R1 to R4 and a trim warning audit. Avalonia's own Android number (vendor) suggests a large gain, but Windows numbers are unknown. | Moderate to high: trimming, third-party controls (terminal, WebView). | Medium: AOT-incompatible libraries; no runtime reflection. Avalonia 12 AOT docs not located. | Microsoft and Avalonia AOT pages (verified). Gain: vendor claim only. |
| R6 | Login autostart on Windows as an opt-in setting, default off, starting minimized to tray or with no window. Use an MSIX `windows.startupTask` if the client ships packaged; else an HKCU Run value. Respect `DisabledByUser`. | Low. | Low: user-visible in Settings > Apps > Startup. Packaging choice is open. | StartupTask and Run-key pages (verified). |
| R7 | Warm hidden process on Windows (Edge-style): not in v1. Revisit only after measuring idle RAM and GPU memory of a hidden Avalonia process. If shipped, it is an operator-visible setting with a documented default. | Continuous RAM and GPU; unknown until measured. | Medium to high: decrypted personal fields resident while the session is locked; stale state; user backlash (Edge default was reported as changed after feedback, secondary). | Edge support page (verified); the cost is unmeasured. |
| R8 | Pre-warm after login only when idle and with enough RAM, loading only non-visual state (fonts, shader caches, the snapshot in R3). Office-style gating. | Moderate. | Low to moderate: login-time disk and CPU spike; Office update re-enable pattern (secondary). | Office Startup Boost (secondary). |
| R9 | Linux: XDG autostart `.desktop` for the GUI client (opt-in). Use `systemd --user` only for a headless helper, and enable linger only if an operator needs it running with no login. | Low. | Unverified: graphical-session environment handling in user units. | Linger text (verified); XDG and unit details unverified. |
| R10 | Android: no resident process. Optimize cold start (baseline profiles and `reportFullyDrawn`), use content-free FCM or UnifiedPush wake-ups, and no foreground service just to stay warm. | Low to moderate. | Medium: per-API-level restrictions; Play policy unverified. | Android pages (verified); baseline profile effect for Avalonia Android unverified. |

Do not:
- Do not ship any always-on hidden process enabled by default.
- Do not change autostart silently; the setting must be visible and reversible (consistent with the DECIDED "never claim a silent default" rule for Windows default apps, applied here by analogy).
- Do not let a warm process run compute or hold secrets; the server does compute (DECIDED).

## 7. Conflicts with DECIDED items and failures

Conflicts: none found. Notes:
- The DECIDED rule "clients render; the server computes" limits R3 and R7 to render-only caches and resident state. Consistent.
- R6 and R7 need Windows-only code. The CLAUDE.md rule that OS-specific code sits behind a provider interface is written for the server; applying it to the client by analogy, these belong in the Windows client head, not in the shared `Bazaar.Client.Core`.
- The Android and iOS minimum is .NET 10 for Avalonia 12 (secondary, from `/home/user/Bazaar/docs/research/notes/Bazaar stack feasibility research/avalonia_clients.md`). Bazaar targets .NET 11; no conflict found.

Failures and gaps:
- 404: `docs.avaloniaui.net/docs/deployment/trimming-and-aot`; `docs.avaloniaui.net/docs/guides/development-guides/compiled-bindings`; `docs.avaloniaui.net/docs/deployment/startup-performance`; `learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-run-desktop-app-background-task`; `learn.microsoft.com/en-us/windows/uwp/launch-resume/run-desktop-app-at-startup`. Avalonia compiled-bindings content was taken from the XAML compilation page instead.
- Empty: XDG autostart spec (after 301 redirect to `specifications.freedesktop.org/autostart/...`).
- Did not render: `support.mozilla.org` background-updates page.
- Not found: Chrome primary support page; Firefox primary on background tasks; Teams and Office primary Microsoft docs; Avalonia 12 Native AOT page.
- Search snippets used as secondary only: Office, Teams, Chrome, Firefox, Android 15 limits, Avalonia 11 claims, lightrun hello-world numbers.

## 8. Open facts (to resolve before any startup design is decided)

1. First-frame breakdown of an Avalonia client on Windows and Linux (window creation, Skia GPU init, font manager, XAML load, first data). No official data found. Needs a benchmark harness. (unverified)
2. Idle RAM and GPU memory of a hidden Avalonia process on Windows. (unmeasured)
3. Whether the Avalonia 12 template still needs `AvaloniaResource` for compiled XAML. (unverified)
4. Avalonia 12 Native AOT and trimming documentation, and whether `BuiltInComInteropSupport=false` still applies. (unverified)
5. Whether Chrome's "continue running" toggle still exists in current stable builds. (unverified)
6. Whether Firefox has any "keep running when closed" setting beyond background updates. (unverified)
7. Edge Startup Boost memory cost, the policy name `StartupBoostEnabled`, and its default state. (secondary only)
8. Whether Settings > Apps > Startup lists app-created Task Scheduler logon tasks. (unverified)
9. XDG autostart key set and behavior in GNOME, KDE and Xfce. (unverified)
10. systemd user-unit behavior without linger, and `WantedBy=default.target` for graphical sessions. (unverified; inferred)
11. Android 15 `dataSync` 6 h / 24 h limit at primary source. (secondary)
12. Google Play policy on foreground services and FCM high-priority wake-ups. (not researched)
13. Whether baseline profiles help Avalonia Android startup. (unverified)
14. Whether the Avalonia 12 Android 1,960 ms to 460 ms figure reproduces on a reference device. (vendor claim)
15. Packaging decision for `Bazaar.Client.Windows` (MSIX or unpackaged): decides StartupTask versus Run key. Owner question; no DECIDED item found that settles it.

## 9. Source list (consolidated)

- https://docs.avaloniaui.net/docs/deployment/native-aot
- https://docs.avaloniaui.net/docs/xaml/compilation
- https://docs.avaloniaui.net/docs/app-development/performance
- https://docs.avaloniaui.net/docs/avalonia12-breaking-changes
- https://docs.avaloniaui.net/xpf/configuration/performance
- https://avaloniaui.net/blog/avalonia-12
- https://wieslawsoltes.github.io/development-plugin-for-avalonia/references/08-performance-checklist (secondary)
- https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run
- https://support.microsoft.com/en-us/edge/get-help-with-startup-boost
- https://support.microsoft.com/en-us/windows/configure-startup-applications-in-windows-115a420a-0bff-4a6f-90e0-1934c844e473
- https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
- https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-start-page
- https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask
- https://www.freedesktop.org/software/systemd/man/latest/loginctl.html
- https://www.freedesktop.org/software/systemd/man/latest/user@.service.html
- https://developer.android.com/topic/performance/vitals/launch-time
- https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start
- https://developer.android.com/guide/components/activities/background-starts
- https://developer.android.com/guide/components/activities/process-lifecycle
- https://mc.merill.net/message/MC1041470 (secondary)
- https://pureinfotech.com/disable-edge-startup-boost-save-ram-windows-11/ (secondary)
- https://www.ghacks.net/?p=170681 (secondary, search only)
- `/home/user/Bazaar/docs/research/notes/Bazaar stack feasibility research/avalonia_clients.md` (read; secondary for Avalonia 12 Android claims)
