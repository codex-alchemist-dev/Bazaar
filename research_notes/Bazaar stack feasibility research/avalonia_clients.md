# Avalonia as the Single UI Framework for Bazaar Windows, Android, and Linux (as of Oct 2026)

Research date: 2026-10-24. Sources are primary where stated; several facts come from search-result snippets I could not open in full, and those are marked. Items marked **[UNVERIFIED]** could not be confirmed from a primary source. Items marked **[VENDOR CLAIM]** come from Avalonia's own marketing and have no published methodology.

Design-doc claims checked (from Bazaar.Windows.md, Bazaar.Android.md, Bazaar.Constellation.md, Bazaar.Linux.md):
- Avalonia 12 shipped a rebuilt Android backend: **verified (vendor source)**, but the specifics the docs attach to it are only partly supported (see Q1).
- Monaco embedded via WebView2 under Avalonia: **not verified** (no source found that shows Monaco running in an Avalonia WebView).
- "Terminal and Monaco/WebView2 embedding both verified as real": **only the terminal part has partial support**; the WebView part is weaker than the doc states.
- "The most polished WebView integration sits under Avalonia XPF": **contradicted** by the sources. The WebView component is documented under Avalonia Accelerate, and the XPF overview page does not mention WebView (see Q4).
- Microsoft has an official guide for .NET + Jetpack WindowManager: **partly verified** (bindings exist; only a Korean-locale Learn page surfaced, see Q6).

---

## Q1. Current Avalonia version, and what the Android backend rebuild actually changed

### Takeaway
Avalonia 12.x is current (latest stable seen: 12.1.4, dated 9 Oct 2026). The "rebuilt Android backend" is real and is described in Avalonia's own Avalonia 12 announcement, but the announcement names only a few concrete internal changes, gives performance numbers with no test conditions, and says nothing about high-refresh-rate scheduling.

### Cited Findings
- Latest stable release is 12.1.4, dated 09 Oct (2026); preceding releases 12.1.3 (22 Sep), 12.1.2 (02 Sep), 12.1.1 (29 Jul), 12.1.0 (09 Jul), 12.0.5 (23 Jun) — [GitHub Avalonia releases](https://github.com/AvaloniaUI/Avalonia/releases) (page fetched; year inferred from the listing and the Wikipedia date below).
- Wikipedia lists 12.1.4 / 9 October 2026 as latest stable — [Wikipedia: Avalonia](https://en.wikipedia.org/wiki/Avalonia_(software_framework)) (search snippet, not opened).
- Avalonia 12.0 was the Android/iOS and general "Performance and Stability" release line; Avalonia.Android 12.0.0 was released April 2026 per NuGet — [NuGet Avalonia.Android](https://packages.nuget.org/packages/Avalonia.Android) (search snippet; the 12.0.0 date is from the snippet, not the page body).
- The Avalonia 12 announcement says the Android changes include: "implemented a proper IDispatcherImpl for Android using Looper and MessageQueue"; "stripped out unnecessary OpenGL synchronisation calls that were adding latency"; "targeted optimisations to CPU and GPU utilisation when frame timing falls behind"; and cleanup of "surface recreation, safe-area inset padding after resume, and the entire app model around AvaloniaActivity" — [Avalonia 12 announcement](https://avaloniaui.net/blog/avalonia-12) (fetched).
- Vendor performance figures from that announcement: startup 1,960 ms → 460 ms with NativeAOT ("4x"); scrolling 42 → 120 FPS; animations "locked 60 FPS"; idle CPU 0.20% → under 0.01% ("20x"); summary "3x performance improvements on Android" — [Avalonia 12 announcement](https://avaloniaui.net/blog/avalonia-12) (fetched). **[VENDOR CLAIM]** The post gives no device models, Android versions, app under test, or measurement method.
- The announcement's Android section does **not** mention high-refresh-rate scheduling. That wording ("reliable scheduling at high refresh rates", "native support for multiple Android activities") appears in the Avalonia "What's new" summary as surfaced by search; I did not open that page directly — [What's new in Avalonia 12](https://avaloniaui.net/avalonia) (search summary only). **[UNVERIFIED at primary source]**
- Breaking change: on Android and iOS only .NET 10 is supported; Android apps must inherit the non-generic `AvaloniaMainActivity` instead of `AvaloniaMainActivity<TApp>` — [Avalonia 12 breaking changes](https://v12.docs.avaloniaui.net/docs/avalonia12-breaking-changes) (search snippet).
- Android-specific 12.1.x release notes are minor (for example, `TextInputOptions.IsSensitive` and `ContentType` support in 12.1.2; a multiline TextBox caret fix; a `content://` URI file-picker fix) — [GitHub releases](https://github.com/AvaloniaUI/Avalonia/releases) (fetched; truncated).

### Inferences
- The rebuild is a substantive engineering change (the threading, GL-sync and surface-lifecycle items are specific and plausible), so the design doc's "rebuilt backend" claim holds.
- The design doc's phrase "large rendering-pipeline gains on complex layouts" does not match the announcement, which reports scrolling, animation, startup and idle-CPU numbers. The "complex layouts" wording should be dropped unless a source is found.
- The high-refresh-rate claim should be treated as unverified until checked against the What's new page or a hands-on test on a 120 Hz foldable.

### Gaps
- The internal details of the rebuild are only summarised; no code-level or changelog-level breakdown was found.
- No independent benchmark of the 3x / 42→120 FPS figures was found.

---

## Q2. Android production readiness, known issues, minimum SDK, high-refresh performance

### Takeaway
Android is supported, but support depends on the OS version: only Android 16 (API 36) is Tier 1, Android 12–15 are Tier 2, and Android 11 and earlier are commercial-support-only. The minimum framework is .NET 10. I found no independent production case study and no authoritative list of open Android bugs.

### Cited Findings
- Android tier table (fetched from the supported-platforms page): Android 16 (API 36, ARM64/x64) Tier 1 "Fully tested and supported"; Android 12–15 (API 31–35, ARM64/ARM32/x64) Tier 2; Android 11 and earlier (API 30 and below) Tier 3 "Commercial support only" — [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) (fetched).
- Minimum .NET for mobile is .NET 10; Avalonia's mobile support "follows the MAUI lifecycle", so it ends when Microsoft drops an OS version; .NET 8 mobile workloads were removed from the .NET 10 SDK — [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) (fetched).
- Avalonia.Android 12.0.4 targets `net10.0-android36.0` — [NuGet Avalonia.Android](https://packages.nuget.org/packages/Avalonia.Android) (snippet).
- The Android 12.1.2 notes include IME-adjacent work (`TextInputOptions.IsSensitive`, `ContentType`) — [GitHub releases](https://github.com/AvaloniaUI/Avalonia/releases) (fetched).
- Soft-keyboard and caret issues on Android were reported in 2024 (for example "Soft Keyboard Reappearance Issue for an already focused TextBox" and text-selection handles on empty TextBoxes on Android). A commenter on PR #17618 (merged Dec 2024) reported that Pinyin input works but Korean did not change — [GitHub issues list, via search snippet] **[UNVERIFIED current status]**.
- Avalonia's text-input docs state that IME-based input is supported, with InputScope handling on Android and iOS — [Text Input and IME](https://docs.avaloniaui.net/docs/input-interaction/text-input) (search snippet).

### Inferences
- A foldable target device running Android 16 would sit on Tier 1; Android 12–15 devices would be Tier 2, which is not the same as unsupported, but it means less guaranteed testing.
- Known-issue risk is concentrated in text input (IME, soft keyboard, caret), which matters for a chat-first mobile app. It is the most likely Android-specific blocker to hit.
- Nothing I found validates the "high-refresh-rate" behaviour on real hardware. Budget a device spike at 90/120 Hz.

### Gaps
- **[UNVERIFIED]** Open Android bug count and severity in the AvaloniaUI/Avalonia GitHub issue tracker (searches surfaced only 2024-era issues).
- **[UNVERIFIED]** Any production apps shipping on Avalonia Android at scale.
- Frame-pacing behaviour on 120 Hz devices is not documented in any source I could open.

---

## Q3. Avalonia.Headless: what it supports, and its limits for agent-driven verification

### Takeaway
Avalonia.Headless is a real, documented way to run the UI in memory with no display and to assert on the visual tree and rendered output (including PNG comparisons). It is a strong fit for agent verification of layout and logic. It does not simulate real OS windowing, real IME, real platform accessibility, or device-specific behaviour such as folds.

### Cited Findings
- Headless testing is documented with xUnit and NUnit integration; required packages are `Avalonia.Headless` and `Avalonia.Themes.Fluent`; it has no real input devices, so input is simulated through extension methods on `Window` — [Avalonia testing docs](https://docs.avaloniaui.net/docs/testing/) and [Headless platform](https://docs.avaloniaui.net/docs/concepts/headless/) (search snippets).
- With the Skia renderer and `UseHeadlessDrawing = false`, the headless platform performs a full in-memory render, usable for image generation outside tests — [Headless platform](https://docs.avaloniaui.net/docs/concepts/headless/) (snippet).
- Visual regression is documented as: render a control, save PNG, compare to baseline within a configurable error tolerance; the docs also cover running headless in Docker and CI — [Avalonia testing docs](https://docs.avaloniaui.net/docs/testing/) (snippet).
- The docs say Appium has no stable Linux desktop driver, so headless is the recommended path for Linux CI — [UI Testing with Appium](https://docs.avaloniaui.net/docs/testing/ui-testing-with-appium) (snippet).
- Third-party `HeadlessTestingFramework` (not Avalonia-official) advertises frame capture during tests — [NuGet HeadlessTestingFramework](https://www.nuget.org/packages/HeadlessTestingFramework) (snippet).
- The "Capturing the last rendered frame" page, which should give the exact screenshot API, did not come back in search — **[UNVERIFIED API name]**.

### Inferences
- Agent verification is credible for: layout at multiple sizes, control states, view-model-to-view bindings, keyboard routing within the app, and pixel regression of Skia-drawn custom views (such as the Constellation graph).
- It is **not** equivalent to Playwright. Playwright drives a real browser; Headless drives Avalonia's own tree. The real risks it cannot catch are: OS-level window management (detached windows, default-handler registration), real ConPTY/PTY behaviour, real IME composition, Android fold posture changes, DPI-scaling differences on the actual display, and real accessibility tree exposure to Narrator/AT-SPI/TalkBack. Those need a separate manual or device-farm layer.
- The design doc's framing ("genuinely equivalent to what Playwright gives web development") overstates the case; "equivalent for in-app rendering and logic" is the defensible wording.

### Gaps
- No source confirmed Headless behaviour for embedded native controls (for example, a NativeWebView or terminal control hosted inside Avalonia). Assume those render as placeholders or are excluded until tested.
- No source confirmed whether Headless exposes an accessibility tree for assertions.

---

## Q4. Licensing: Avalonia core, Accelerate, XPF, pricing, and WebView/Monaco

### Takeaway
The Avalonia core is MIT and free for Bazaar. The costs and traps are in the paid tiers. XPF is a separate commercial product for WPF codebases and does not appear to be what the design doc needs. The free-to-use WebView options are less mature than the design doc implies, and I found no source showing Monaco working inside any of them on Linux or Android.

### Cited Findings
- Avalonia (free tier) is described as the "MIT licensed UI framework" with 70+ built-in controls — [Avalonia pricing](https://avaloniaui.net/pricing) (fetched).
- Paid per-seat tiers (EUR, excl. VAT, annual billing): **Plus €299/yr per seat** (IDE tooling: Visual Studio extension, DevTools, Parcel, Hot Reload); **Pro €899/yr per seat** (adds Media Player, Tree DataGrid, Markdown Viewer, Rich Text Editor, On-Screen Keyboard, PDF Viewer, 70+ charts); **Enterprise from €21,000/yr** (three seats, contracted response times, 5 years of LTS security updates, full source for premium controls, capped IP indemnity) — [Avalonia pricing](https://avaloniaui.net/pricing) (fetched).
- Seat rule for Plus/Pro: "The total number of developers using Plus or Pro at any given time must not exceed the number of purchased seats"; seats reassignable after a 90-day cooldown; Enterprise seats are "for developers doing UI work, not the whole team" — [Avalonia pricing](https://avaloniaui.net/pricing) (fetched).
- The pricing page states no revenue or company-size thresholds and no open-source exemption for Plus/Pro/Enterprise — [Avalonia pricing](https://avaloniaui.net/pricing) (fetched).
- **Gotcha for Bazaar:** Rich Text Editor, Tree DataGrid, Markdown Viewer and PDF Viewer are Pro-tier premium controls. If the chat, Vault or Kanban UI uses any of them, the per-seat fee applies, and the seat limit counts developers "using" them at any moment.
- XPF is "a commercial product that swaps out WPF's low-level rendering component (MilCore) with Avalonia's rendering engine"; Windows, macOS and Desktop Linux are Tier 1; mobile/WebAssembly appear only in the Enterprise tier per the licence table; the XPF overview does not mention WebView or the MIT core — [Avalonia XPF welcome](https://docs.avaloniaui.net/xpf/welcome) (fetched). The XPF pricing page returned HTTP 404 on fetch.
- XPF prices from search snippets (**[UNVERIFIED]**; third-party aggregators disagree with each other and with the official snippet): Business €19,500 (perpetual, no revenue limit, 3-working-day SLA); Internal ~€7,800 (aggregator only); Enterprise ~€89,000 (aggregator only, 1-working-day SLA). A single XPF licence covers all supported desktop platforms since Feb 2024; Enterprise licence schedule v1.5 effective 5 Feb 2026 — [XPF licence change blog](https://avaloniaui.net/Blog/avalonia-xpf-license-changes) and [XPF pricing](https://avaloniaui.net/xpf/pricing) (snippets only; the page itself 404'd).
- `Avalonia.Controls.WebView` (NativeWebView) latest seen: 12.0.1. Engines: WebView2 (Windows), WebKit (macOS), WebKitGTK (Linux) — but the Linux build is WPE WebKit rendered offscreen; Android uses Xamarin.Android bindings. The package **requires the Avalonia Accelerate licence** — [Avalonia WebView docs](https://docs.avaloniaui.net/accelerate/components/webview/nativewebview) and [NuGet Avalonia.Controls.WebView](https://feed.nuget.org/packages/Avalonia.Controls.WebView/) (snippets). The Accelerate price and tier are not stated in what I could read.
- Community alternatives (snippets; licences not checked):
  - `WebView2.Avalonia` — prerelease; NuGet lists Windows and macOS only, so no Linux/Android — [NuGet WebView2.Avalonia](https://www.nuget.org/packages/WebView2.Avalonia).
  - `Avalonia.WebView` (MicroSugarDeveloperOrg) — claims all four targets: WebView2 on Windows, WebKitGTK on Linux, Android WebView — [GitHub MicroSugarDeveloperOrg/Avalonia.WebView](https://github.com/MicroSugarDeveloperOrg/Avalonia.WebView). Maturity and licence unverified.
- Dock.Avalonia (docking/layout library) is MIT; current release 12.1.0; Avalonia 12 packages are the unsuffixed ones, and `.v11` packages are for Avalonia 11.3.20 and must not be mixed — [Dock repo](https://github.com/wieslawsoltes/Dock) and [NuGet Dock.Model.ReactiveUI 12.1.0](https://www.nuget.org/packages/Dock.Model.ReactiveUI/12.1.0) (snippets).

### Inferences
- Use of Avalonia core, Plus/Pro, and Dock is compatible with an open-source Bazaar **only if** the seat and licence terms are acceptable for the project's licensing model. Avalonia's licence does not exempt open-source projects that use paid controls. Confirm with Avalonia legal before shipping any Pro control.
- The design doc's "budget for paid XPF" framing is probably wrong for the WebView question. The relevant paid item is **Accelerate** (for `NativeWebView`), and it is not XPF. Confirm the Accelerate price and whether it covers Android and Linux.
- Without Accelerate, the free WebView path is `WebView.Avalonia` (community, unverified) or a hand-rolled host of WebView2 (Windows only) plus a separate Linux/Android solution. This is a larger, riskier build.
- Monaco embedding is a separate risk from WebView choice. Monaco needs a working JS bridge, and the Linux WebKitGTK/WPE path and Android WebView path would each need their own testing, including keyboard focus and IME inside the editor.

### Gaps
- **[UNVERIFIED]** Accelerate licence tier, price, and terms (not found in what I could open).
- **[UNVERIFIED]** Whether Monaco runs in any Avalonia WebView on any platform. No source found.
- **[UNVERIFIED]** Licence and maintenance status of `Avalonia.WebView` (MicroSugarDeveloperOrg) and `Iciclecreek.Avalonia.Terminal`.
- Avalonia's own legal page (avaloniaui.net/legal, referenced by the pricing page) was not opened.

---

## Q5. Terminal controls: Iciclecreek.Avalonia.Terminal and others; ConPTY and PTY

### Takeaway
Community Avalonia terminal controls exist, but none of the ones I could check is established as production-grade, and the licence of the leading candidate is unconfirmed. The design doc's "production-grade ConPTY-based" claim is not supported by anything I could open. For fidelity, WinUI 3's `EasyWindowsTerminalControl` (which wraps the Windows Terminal rendering code) remains the stronger option; this is a real cost of the Avalonia move.

### Cited Findings
- `Iciclecreek.Avalonia.Terminal` NuGet README (snippet): an XTerm-compatible terminal control for Avalonia built on XTerm.NET and Porta.Pty; it shows a licence badge, but the licence name was not visible — NuGet/README (snippet, no URL opened). The GitHub repo fetch returned HTTP 404 (I guessed the URL; the actual URL was not confirmed).
- `Terminal.Avalonia` / `Terminal.Pty` (NuGet): backends for ConPTY and forkpty; licence not shown in the snippet — [NuGet Terminal.Pty](https://www.nuget.org/packages/Terminal.Pty), [NuGet Terminal.Avalonia](https://www.nuget.org/packages/Terminal.Avalonia) (snippets).
- `AvaloniaTerminal` 1.0.0 and `MTerminal.Avalonia.Terminal` 2.7.8 also exist on NuGet — [NuGet AvaloniaTerminal](https://packages.nuget.org/packages/AvaloniaTerminal/) (snippet). Maturity and licence unverified.
- `Exclr8.Terminal` is MIT but has no ConPTY backend (snippet).
- Design doc claim "WinUI 3 `EasyWindowsTerminalControl` wraps the actual Windows Terminal rendering code": not checked in this pass; treat as the doc's claim.

### Inferences
- A ConPTY backend on Windows and a PTY backend on Linux are both available in the .NET ecosystem (Terminal.Pty-style packages), so the mechanics are feasible.
- The hard parts for a production terminal are alt-screen handling, mouse reporting, selection, bracketed paste, wide and CJK characters, Sixel/images, and IME inside the terminal. None of these were verified for any candidate.
- Plan a spike with two candidates, and fall back to a custom xterm-compatible emulator on top of a PTY if neither holds up.

### Gaps
- **[UNVERIFIED]** Licence, maintenance activity and conformance tests for every candidate. The Iciclecreek GitHub page did not open.
- **[UNVERIFIED]** Whether any Avalonia terminal control supports Android (no PTY on Android in the usual sense, which may mean the terminal is not a requirement there).

---

## Q6. Foldable support: Jetpack WindowManager via .NET for Android, posture changes, official Avalonia guidance

### Takeaway
The Android-side plumbing is real: Microsoft publishes .NET for Android bindings for Jetpack WindowManager (`Xamarin.AndroidX.Window`), and `WindowInfoTracker` / `FoldingFeature` are accessible from .NET. I found no Avalonia-specific foldable guidance, and no source showing how Avalonia reacts to posture changes. This is an unverified integration point that needs a spike.

### Cited Findings
- Microsoft's .NET for Android bindings: `Xamarin.AndroidX.Window` (1.3.0.4) and `Xamarin.AndroidX.Window.WindowCore` (1.5.1.3); the NuGet listings show `net10.0-android36.0` compatible — [NuGet Xamarin.AndroidX.Window.WindowCore](https://www.nuget.org/packages/Xamarin.AndroidX.Window.WindowCore) and [NuGet Xamarin.AndroidX.Window](https://nuget.org/packages/Xamarin.AndroidX.Window) (snippets).
- Microsoft's Surface Duo blog (June 2021) introduces the Jetpack WindowManager preview for Xamarin and says a `WindowJava` package is better suited to binding — [Microsoft Dev Blogs](https://devblogs.microsoft.com/surface-duo/xamarin-jetpack-window-manager-preview) (snippet; 2021, so may be dated).
- Microsoft Learn's Jetpack WindowManager page (surfaced only in the ko-kr locale) describes `WindowInfoTracker` and `FoldingFeature` (`BoundsIsSeparating`, `OcclusionType`, `Orientation`, `State`) — [Microsoft Learn (ko-kr)](https://learn.microsoft.com/ko-kr/dual-screen/xamarin/jetpack-window-manager) (snippet). The design doc's claim of an "official guide for exactly this .NET + Jetpack WindowManager scenario" is therefore only partly verified: the guide exists, but it is a 2021-era Xamarin/Surface Duo document, not an Avalonia guide.
- Support boundary: Microsoft supports the bindings; the underlying `androidx.window` library is maintained by Google — NuGet listings (snippet).

### Inferences
- Foldable detection is achievable without Avalonia: read `WindowLayoutInfo` via the bindings, pass fold bounds and state into the view model, and let the Avalonia layout react. This is an app-level pattern, not a framework feature.
- The Android design (folded = chat, unfolded = panes) depends on how the Avalonia `Window`/`Activity` handles configuration changes and resizing on fold/unfold, which is a separate question from the bindings. I found nothing on this.
- A real fold posture change (tabletop, half-open) needs a physical foldable to test, because Headless has no fold events.

### Gaps
- **[UNVERIFIED]** How Avalonia handles fold/unfold configuration change (activity recreation vs in-place resize) on Android 16. No source found.
- **[UNVERIFIED]** Whether there is any official Avalonia foldable guidance. None found.
- The `WindowJava` binding and the current state of `Xamarin.AndroidX.Window` (last updated versions) should be checked against the NuGet pages.

---

## Q7. Dock/tiling panels, Vim-style modal navigation, detachable windows and OS default-handler registration

### Takeaway
Dock.Avalonia (MIT) provides dockable/tiling layout and is current for Avalonia 12. Vim-style modal input is a matter of app-level input routing and is feasible in Avalonia, but I found no source. Detachable windows and OS default-handler registration are plausible on Windows and Linux but were not researched in this pass; they are the least verified part of the design.

### Cited Findings
- Dock.Avalonia is MIT and at version 12.1.0 — [Dock repo](https://github.com/wieslawsoltes/Dock) and [NuGet Dock.Model.ReactiveUI 12.1.0](https://www.nuget.org/packages/Dock.Model.ReactiveUI/12.1.0) (snippets).
- Avalonia's text input docs cover IME-capable text input, which is the input path a modal layer has to coexist with — [Text Input and IME](https://docs.avaloniaui.net/docs/input-interaction/text-input) (snippet).

### Inferences
- A modal (Vim-style) layer is best implemented as a preview-key handler at the window root that swallows keys in normal mode and passes them through in insert mode. This is an app-level pattern, not a library feature. It must be checked against IME composition on Windows and Linux (IME bypasses normal key events during composition).
- Detached windows are feasible with Avalonia multi-window support, but it has not been verified that a detached window can be registered as the OS default handler for "open file in editor" or "default terminal". That needs OS shell work (Windows file association and registry/AppX registration; on Linux, `.desktop` files and `xdg-mime`), which is independent of Avalonia.

### Gaps
- **[UNVERIFIED]** Dock.Avalonia's detachable floating-window behaviour on Avalonia 12, and whether it is stable.
- **[UNVERIFIED]** Any existing Avalonia app that registers as a default terminal or editor.
- Vim-mode IME interaction is untested.

---

## Q8. Performance of a custom Skia force-directed graph with thousands of nodes

### Takeaway
Not researched with sources in this pass. Avalonia renders through Skia, which makes a custom canvas feasible in principle, but no source gives a frame-time figure for a force-directed graph at thousands of nodes. This needs a spike.

### Cited Findings
- Avalonia renders via Skia on Windows and Linux (Linux via OpenGL + Skia, per the supported-platforms page) — [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) (fetched).
- Headless Skia rendering is available for bitmap-based tests (Q3).

### Inferences
- The layout (force simulation) can run off the UI thread, with rendering limited to visible nodes and batched draw calls. This is a standard approach, not an Avalonia-specific result.

### Gaps
- **[UNVERIFIED]** Frame-time numbers for N = 1k / 5k / 10k nodes on Avalonia 12 Skia on desktop or Android. No source found.
- **[UNVERIFIED]** Android GPU performance for the same graph.

---

## Q9. Known blockers vs WinUI 3: accessibility, IME, high DPI, rendering fidelity, startup, AOT/trimming

### Takeaway
Avalonia now has a native Linux accessibility backend (AT-SPI2) shipped in 12.0, but I found nothing on Android or Windows accessibility parity with WinUI 3. Startup and AOT numbers come from a vendor announcement. High DPI, rendering fidelity and trimming were not verified in this pass.

### Cited Findings
- "Avalonia 12.0 is the first .NET UI framework to ship a native Linux accessibility backend" (AT-SPI2) — [Avalonia 12 announcement](https://avaloniaui.net/blog/avalonia-12) (fetched). **[VENDOR CLAIM]** "first" is not independently checked.
- No accessibility statement for Windows (UIA) or Android (TalkBack) was found on the supported-platforms page or the announcement — [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) (fetched).
- Startup with NativeAOT on Android: 1,960 ms → 460 ms (vendor figure, see Q1) — [Avalonia 12 announcement](https://avaloniaui.net/blog/avalonia-12) (fetched).
- Windows support: Win11 24H2 Tier 1; Win10 22H2 Tier 2; Win11 22H2 Tier 2 — [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) (fetched).
- Linux: X11 direct; Wayland as an opt-in package from 12.1.0 (no tier given); Tier 1 Ubuntu 25.x, Fedora 43, Debian 13; Tier 3 for Arch, Alpine, NixOS and other non-standard distros, including musl-based ones that need a custom Skia build — [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) (fetched).
- IME: documented as supported (Q2); no documented IME test results on Windows or Linux.

### Inferences
- Linux accessibility looks better than the design doc assumes (AT-SPI2 exists), but Windows UIA and Android TalkBack are unverified, and an accessibility gap is a real risk for a team that cares about a "native feel".
- Linux on Wayland is a 12.1.0 opt-in and may need testing for IME and fractional scaling. Choose X11 as the default unless Wayland is tested.
- The "not Electron-class" claim is plausible (Skia, no bundled browser), but the trade-off is that Avalonia draws its own controls, so native look, feel and accessibility are not inherited, as the design doc says.

### Gaps
- **[UNVERIFIED]** Windows UIA and Android TalkBack accessibility coverage in Avalonia 12.
- **[UNVERIFIED]** High-DPI / mixed-DPI behaviour on Windows and fractional scaling on Linux.
- **[UNVERIFIED]** AOT trimming warnings and size for a full Bazaar UI (only the Android startup number exists, and it is vendor-supplied).
- **[UNVERIFIED]** Rendering fidelity of text (ligatures, bidi, emoji) compared with WinUI 3's DirectWrite path.

---

## Overall risk summary (for the report writer)

Ranked by how much each risk could change the decision:

1. **Android text input and IME** on Avalonia (chat-first mobile app). Unverified current state; 2024-era soft-keyboard issues; Korean IME on Android was reported unresolved in one comment.
2. **Monaco embedding** (design doc depends on it). No source shows it working in any Avalonia WebView. The free WebView options are community projects with unverified maturity; the official one needs Accelerate. The design doc's "XPF has the best WebView" claim is contradicted.
3. **Terminal control maturity** (no candidate verified as production-grade; licence of the lead candidate unconfirmed).
4. **Licensing traps**: Plus/Pro per-seat, premium controls (Rich Text Editor, Tree DataGrid, PDF, Markdown), Accelerate (WebView), XPF (not needed for this use). Prices above are list prices from Avalonia's pricing page, except the XPF and Accelerate figures.
5. **Foldable fold/unfold behaviour in Avalonia** (no source; the bindings exist).
6. **Android version coverage**: Tier 1 only Android 16; Tier 2 for 12–15.
7. **Accessibility on Windows and Android** (only Linux AT-SPI2 is verified).
8. **Headless verification** is strong for in-app rendering and logic; it cannot cover OS windowing, IME, ConPTY, fold events or real DPI.

Items confirmed as fact (primary source): Avalonia 12.x line and 12.1.4 release date; Android backend rebuild announced with vendor performance numbers; Android tier table; .NET 10 minimum for mobile; Linux AT-SPI2 backend; Plus/Pro/Enterprise pricing and seat rules; MIT licence of the core and Dock; Headless packages and simulation model.

Items I could not verify: Monaco in any Avalonia WebView; Accelerate pricing; XPF prices (only aggregator/snippet); terminal-library licences and maturity; high-refresh-rate behaviour on real devices; fold handling in Avalonia; Avalonia accessibility on Windows and Android; graph performance; default-handler registration.

## Sources consulted (primary unless marked)
- https://avaloniaui.net/blog/avalonia-12 (fetched)
- https://avaloniaui.net/avalonia (What's new; search summary only)
- https://v12.docs.avaloniaui.net/docs/avalonia12-breaking-changes (snippet)
- https://github.com/AvaloniaUI/Avalonia/releases (fetched)
- https://en.wikipedia.org/wiki/Avalonia_(software_framework) (snippet)
- https://packages.nuget.org/packages/Avalonia.Android (snippet)
- https://docs.avaloniaui.net/docs/supported-platforms (fetched)
- https://avaloniaui.net/pricing (fetched)
- https://avaloniaui.net/xpf/pricing (404 on fetch; snippet only)
- https://docs.avaloniaui.net/xpf/welcome (fetched)
- https://avaloniaui.net/Blog/avalonia-xpf-license-changes (snippet)
- https://docs.avaloniaui.net/accelerate/components/webview/nativewebview (snippet)
- https://feed.nuget.org/packages/Avalonia.Controls.WebView/ (snippet)
- https://www.nuget.org/packages/WebView2.Avalonia (snippet)
- https://github.com/MicroSugarDeveloperOrg/Avalonia.WebView (snippet)
- https://docs.avaloniaui.net/docs/testing/ and https://docs.avaloniaui.net/docs/concepts/headless/ (snippets)
- https://docs.avaloniaui.net/docs/testing/ui-testing-with-appium (snippet)
- https://www.nuget.org/packages/HeadlessTestingFramework (snippet)
- https://docs.avaloniaui.net/docs/input-interaction/text-input (snippet)
- https://github.com/wieslawsoltes/Dock and https://www.nuget.org/packages/Dock.Model.ReactiveUI/12.1.0 (snippets)
- https://www.nuget.org/packages/Terminal.Pty and https://www.nuget.org/packages/Terminal.Avalonia (snippets)
- https://packages.nuget.org/packages/AvaloniaTerminal/ (snippet)
- https://www.nuget.org/packages/Xamarin.AndroidX.Window.WindowCore and https://nuget.org/packages/Xamarin.AndroidX.Window (snippets)
- https://devblogs.microsoft.com/surface-duo/xamarin-jetpack-window-manager-preview (snippet)
- https://learn.microsoft.com/ko-kr/dual-screen/xamarin/jetpack-window-manager (snippet)
