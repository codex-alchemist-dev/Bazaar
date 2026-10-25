# Bazaar client UI framework contingency (as of 2026-10-25)

Research date: 2026-10-25. Budget used: about 23 tool calls. Labels: **verified** = primary source read in this pass (fetch succeeded); **secondary** = search snippet or third-party article; **unverified** = not confirmed in this pass. Several framework doc pages returned 404 (noted where they matter). Nothing was installed or run.

Context read first: `docs/research/notes/Bazaar stack feasibility research/avalonia_clients.md`, `docs/design/27-client-architecture.md` (Conflicts 1), `docs/design/28-windows-client.md` (Conflicts 1, 2, 3 and the Monaco hosting table).

## Key Question 1. Within .NET: which alternatives keep the client in C#?

### Takeaway
Within .NET, no alternative gives a verified free, Windows + Android + Linux route to Monaco. .NET MAUI's BlazorWebView is the most mature WebView host but has no official Linux desktop target. Uno's Linux (Skia GTK) head is being retired and its WebView coverage is unconfirmed (Uno WebView docs returned 404). Eto.Forms has no WebView evidence. AvaloniaEdit (MIT, TextMate grammars) is the only .NET option that is a native editor rather than a web-hosted one; it is a credible Monaco replacement for editing but lacks a diff view and LSP-grade services.

### Cited Findings
- .NET MAUI BlazorWebView "can be added to any page ... The Razor components run natively in the .NET process and render web UI to an embedded web view control. In .NET MAUI, Blazor Hybrid apps can run on all the platforms supported by .NET MAUI" — [MS Learn: BlazorWebView](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/blazorwebview) (**verified**, page dated 2026-09-02 in its metadata).
- MAUI WebView engines: Windows WebView2; Android `android.webkit.WebView`; iOS and Mac Catalyst WKWebView. Windows end users must install the WebView2 runtime — same page (**verified**). Linux is not listed among MAUI WebView engines on that page (**verified** by absence).
- The BlazorWebView static-cache property (net-maui-11.0 only) is "supported on Android, iOS, Mac Catalyst, Windows, and Tizen" — same page (**verified**).
- No official Microsoft Linux desktop backend for MAUI. Community options: `open-maui/maui-linux` (SkiaSharp, described as MIT with X11/Wayland, "47+ controls"; "production-ready" is a promotional claim) — [Byteiota summary](https://byteiota.com/net-maui-linux-support-two-paths-forward-in-2026/) (**secondary**); `OpenMaui.Controls.Linux` NuGet, latest listed 10.0.70.x, updated July 2026 — [NuGet OpenMaui.Controls.Linux](https://packages.nuget.org/packages/OpenMaui.Controls.Linux/10.0.70.1) (**secondary**, snippet); Maui.Gtk (GTK4) described as experimental, March 2026 — [Phoronix](https://www.phoronix.com/news/Microsoft-dotNET-MAUI-GTK4) (**secondary**, snippet).
- Avalonia-based MAUI backend for Linux and browser: reported 2025-11-13 — [The Register](https://www.theregister.com/2025/11/13/dotnet_maui_linux_avalonia/) (**secondary**, snippet only; not opened). Its WebView behaviour on Linux is **unverified**.
- Uno Platform: the Skia+GTK head is "being retired as of Uno Platform 5.2" and apps should move to Skia Desktop — [Uno docs: Using the Skia+GTK head](https://platform.uno/docs/articles/features/using-skia-gtk.html) (**secondary**, search snippet; page not fetched). Uno 4.9 added WebView2 support, and the search snippet ties Linux/Skia WebView to "native host" embedding only loosely — [Uno 4.9 blog](https://platform.uno/blog/4-9-release-media-player-element-on-mobile-web-linux-webview2-support/) (**secondary**). Uno WebView doc pages returned **404** (`platform.uno/docs/articles/features/webview.html` and `working-with-webview.html`), so Uno's WebView platform matrix is **unverified**. Uno's Apache-2.0 licence was not checked in this pass (**unverified**).
- Eto.Forms: GTK platform targets GTK 3.14+ for Linux; Windows via WinForms/WPF; NuGet 2.11.0 updated March 2026 — [NuGet picoe profile](https://packages.nuget.org/profiles/picoe) (**secondary**). No WebView control was found in any result (**gap**; Eto wiki and `github.com/picoe/Eto` not opened).
- AvaloniaEdit: "MIT license"; syntax highlighting "using TextMate grammars and themes" via the `AvaloniaEdit.TextMate` package; code folding listed; diff not mentioned; IME not mentioned; Releases section empty on the page — [AvaloniaEdit repo](https://github.com/AvaloniaUI/AvaloniaEdit) (**verified**, fetched).
- Avalonia's own WebView docs describe `NativeWebView` with `NavigateToString`, `InvokeScript` and a message bridge — [Avalonia docs: NativeWebView](https://docs.avaloniaui.net/accelerate/components/webview/nativewebview) (**secondary**, snippet from search; page not fetched). Same search: no example of Monaco in any Avalonia WebView was found (**gap**).

### Inferences
- The only .NET route that is both free and maps to all three heads is a native editor (AvaloniaEdit-class) plus no web view for the editor, or a web view that is free on each OS. Neither is verified for all three heads today.
- MAUI BlazorWebView would be the strongest Windows/Android web host, but it would also turn the whole UI into Blazor (web technology) and still lacks official Linux. It does not solve Linux and moves the client into a web-UI model, which conflicts with the "one Avalonia codebase" decision.
- Uno's Linux story is moving away from the GTK head, so Uno is a weak fallback for Linux unless Skia Desktop WebView support is confirmed.

### Gaps
- Uno WebView per-platform matrix (docs 404); Uno licence.
- Eto.Forms WebView support on any platform.
- Whether `OpenMaui.Controls.Linux` or the Avalonia-based MAUI backend host WebView on Linux.
- Custom Skia/GPU force-directed graph support, foldable APIs, IME maturity and headless testing for MAUI, Uno, Eto were **not researched** in this pass. Avalonia's own values are in `avalonia_clients.md`.

## Key Question 2. Outside .NET, and the cost of migrating only the clients

### Takeaway
Outside .NET, the best-documented free WebView engines are the system ones (WebView2, WebKitGTK, Android System WebView, WKWebView) reached through Tauri-style shells, which is the most reliable way to run Monaco on Windows and Linux. Those shells make the UI web technology inside a native wrapper, which conflicts with the "no web client" and "clients render" rules and needs an owner decision. Compose Multiplatform, Qt and Flutter each have a desktop WebView gap or a licence or maturity question. Migration of the clients alone is feasible because the server stays .NET, but the shared Contracts library would have to be consumed through a language-neutral contract, and the wire format is still OPEN.

### Cited Findings
- Tauri WebView engines: Windows WebView2 (Chromium), macOS and iOS WKWebView, Linux WebKitGTK (`webkit2gtk`), Android system WebView "based on Chromium"; Android runtime version "depends on the device's currently selected WebView provider" — [Tauri docs: WebView versions](https://v2.tauri.app/reference/webview-versions/) (**verified**, fetched). Linux page caveat: "very hard to compile accurate information about WebKitGTK on the various distros."
- Monaco: MIT licence (**verified**, fetched). Monaco's own FAQ: "Is the editor supported in mobile browsers or mobile web app frameworks?" Answer: "No." (**verified**, fetched from the repo README/FAQ). Monaco does not claim direct LSP; providers "often map to language server protocol features"; TextMate grammars are not supported directly, pointing to the third-party `monaco-tm` (**verified**, fetched). Web workers do not load from `file://` (**verified**).
- Monaco mobile failures in practice (anecdotal, **secondary**): a Mozilla bug reporting duplicated characters in the Monaco playground on Android, closed as low priority with the same "not supported in mobile browsers" citation — [Bugzilla 1673396](https://bugzilla.mozilla.org/show_bug.cgi?id=1673396) (search snippet); a Babylon.js playground report of lost caret on a Pixel 4 — [Babylon forum](https://forum.babylonjs.com/t/playground-editor-issue-on-android/19608) (snippet).
- Android IME in contenteditable-based editors is inconsistent: Gboard produced no composition events in 2017 W3C testing — [W3C editing TF archive](https://lists.w3.org/Archives/Public/public-editing-tf/2017Jun/0004.html) (**secondary**, snippet); ProseMirror team notes Android key events often lack key information — [ProseMirror forum](https://discuss.prosemirror.net/t/contenteditable-on-android-is-the-absolute-worst/3810) (**secondary**, snippet). These are old; current Chromium behaviour is **unverified**.
- CodeMirror 6: its 2021 project page says the rewrite "greatly improves" accessibility and touchscreen support using a native editable control — [CodeMirror 6 (Wayback, 2021)](https://webcf.waybackmachine.org/web/20211102230227/https://codemirror.net/6) (**secondary**, snippet). CodeMirror 5.0 notes say contentEditable is needed on mobile for native selection — [CodeMirror forum: 4.13 and 5.0](https://discuss.codemirror.net/t/new-releases-4-13-and-5-0/163) (**secondary**). CodeMirror 6 licence (MIT) **unverified** in this pass.
- Qt WebEngine: dual-licensed; commercial, or LGPL v3 / GPL v3 / GPL v2 for the Qt-specific parts; the Chromium part carries LGPL 2.1 constraints — [Qt WebEngine licensing](https://doc.qt.io/qt-6.9/qtwebengine-licensing.html) (**secondary**, search snippet; not fetched). Qt for Android terms not found (**gap**).
- Compose Multiplatform: 1.12.0 announced 2026-08-26 — [API Evangelist summary](https://blogs.apievangelist.com/blogs/jetbrains-2026-08-26-compose-multiplatform-1-12-0-released/) (**secondary**); the official compatibility page (1.11.0) lists Linux x86-64 and arm64 — [Kotlin compatibility](https://kotlinlang.org/docs/multiplatform/compose-compatibility-and-versioning.html) (**secondary**, snippet). Desktop WebView: KCEF is the community route and a Kotlin Slack thread says its desktop support is "still work in progress" for one library and calls KCEF "pretty heavy weight" (opinion, **secondary**); JxBrowser 8.0 is a commercial Chromium component covering Windows, macOS and Linux — [TeamDev blog](https://teamdev.com/jxbrowser/blog/web-view-for-compose-multiplatform/) (**secondary**, vendor).
- JetBrains discontinued Fleet on 2025-12-22; a successor agentic tool (Air) is in preview — [InfoWorld](https://www.infoworld.com/article/4104991/jetbrains-discontinues-fleet-ide.html) and [AlternativeTo](https://alternativeto.net/news/2025/12/jetbrains-will-discontinue-its-fleet-ide-on-december-22-2025-due-to-overlapping-purposes/) (**secondary**, snippets). Fleet is therefore not a usable reference product; its UI stack is not a route.
- Flutter: community desktop WebView packages exist. `webview_all` lists Windows (WebView2, Win10 1809+) and Linux (webkit2gtk-4.1) — [pub.dev webview_all](https://pub.dev/packages/webview_all/versions/1.1.1) (**secondary**, snippet). `webview_win_floating` says Flutter widgets cannot render above its WebView on Windows and Linux — [pub.dev webview_win_floating](https://pub.dev/documentation/webview_win_floating/latest/) (**secondary**). `webview_flutter_windows` covers Windows only — [pub.dev](https://pub.dev/packages/webview_flutter_windows/versions/1.1.0/score) (**secondary**). The official `webview_flutter` platform list was not confirmed (**unverified**).
- Zed/GPUI: editor GPL-3.0, collaboration server AGPL-3.0, GPUI Apache-2.0 (one blog says Apache/MIT, conflicting) — secondary sources only, e.g. [Zed 1.0 write-up](https://chatgate.ai/fr/post/zed-1-0) (**secondary**). One aggregator still lists Windows as in progress; a Zed 1.0 article says feature parity across macOS, Linux and Windows on 2026-04-29 (**secondary**, conflicting). Zed is a Rust editor, not a client framework for an external app; its GPUI is Rust-only.
- Slint: triple licence, GPLv3 OR Royalty-free 2.0 OR commercial — [Slint licence text, mirrored by Espressif](https://components.espressif.com/components/slint/slint/versions/1.13.0/license) (**secondary**, primary text mirrored). Android, Linux and Windows targets are not named in the licence text. No WebView support was checked (**gap**).

### Inferences
- Tauri is the most mature free route for Monaco on Windows (WebView2) and Linux (WebKitGTK), but Android is the weakest link twice: the system WebView varies by provider, and Monaco is not supported on mobile per its own FAQ. A Tauri client would not solve the Android editor problem.
- Migration of clients only is plausible: the .NET server and its REST or WebSocket surface can stay as is. The cost is one client rewrite per head (Windows, Linux, Android) plus the shared UI library, which for Bazaar is the largest part of the client code.
- The shared Contracts library can be consumed from another language only through a language-neutral schema: JSON Schema or Protobuf (Protobuf tooling is deliberately not added yet; wire format is OPEN in `CLAUDE.md`). Choosing the schema now would make a later client migration cheap. This is a decision for the owner, not a conclusion of this note.
- Compose Multiplatform gives a Kotlin client on Android with a strong Android story, but its desktop WebView needs a commercial component or a community library with unverified maturity. Qt needs C++ or Python and carries LGPL/commercial licensing for QtWebEngine.

### Gaps
- Flutter official WebView platform list; React Native; Godot as a UI; Electron; .NET-adjacent Flutter IME and accessibility, none researched.
- Qt for Android terms and Qt for Python maturity.
- Whether Compose Multiplatform 1.12 changed desktop WebView support.
- Current Chromium Android IME behaviour inside WebView (old 2017 sources only).

## Key Question 3. Is Monaco the right editor? Alternatives and what is lost

### Takeaway
Monaco is a good desktop editor (MIT, VS Code core, language workers) but it is explicitly not for mobile browsers, and it has no native LSP or TextMate support. CodeMirror 6 is the better-documented mobile-capable web editor. A native editor (AvaloniaEdit) keeps the single Avalonia codebase but has no diff view in its README and no LSP. Moving off Monaco loses VS Code-grade language features and a ready-made diff view unless they are rebuilt.

### Cited Findings
- Monaco: MIT; language services in web workers; not for mobile browsers; TextMate via `monaco-tm`; no native LSP (see Q2) — [microsoft/monaco-editor](https://github.com/microsoft/monaco-editor) (**verified**).
- AvaloniaEdit: MIT; TextMate; folding; no diff listed — [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) (**verified**).
- CodeMirror 6 mobile intent (secondary, see Q2) — [CodeMirror 6 (Wayback)](https://webcf.waybackmachine.org/web/20211102230227/https://codemirror.net/6).
- Monaco diff editor and `monaco-languageclient` (LSP bridge) are known Monaco features but were **not verified** in this pass (**unverified**). The design doc's diff view depends on the Monaco diff mode (28 Practices, "Diffs: Monaco diff mode").

### Inferences
- For Windows and Linux the Monaco route stays the strongest for editing, diff and language workers, provided the WebView host works. For Android, Monaco is out by its own FAQ, so the Android head needs a different solution regardless of framework (for example read-only or a light native editor), which is consistent with 27 ("Android: Not included" for the editor).
- A native editor fallback would need to rebuild: diff rendering, LSP client, and find/replace polish. That cost is larger than the WebView risk for Windows, and it is not verified to be cheaper.

### Gaps
- No verified AvaloniaEdit IME, Android or diff evidence.
- Monaco diff editor and LSP bridge not checked in this pass.

## Key Question 4. Evidence on how real cross-platform IDE-like apps are built

### Takeaway
Evidence in this pass is thin. Real IDEs generally use one of two patterns: a web-engine shell around a web editor (VS Code and Electron-style; also Tauri shells), or a native or custom-rendered UI (Zed/GPUI, JetBrains Compose-based Fleet, now discontinued). I did not find verified case studies for Avalonia-based IDEs or for AvaloniaEdit users.

### Cited Findings
- VS Code is Electron-based with Monaco (secondary; common knowledge, not fetched in this pass; **unverified** for this note).
- JetBrains Fleet: discontinued 2025-12-22 — [InfoWorld](https://www.infoworld.com/article/4104991/jetbrains-discontinues-fleet-ide.html) (**secondary**). Its discontinuation is evidence that a multi-platform custom UI IDE was not commercially sustained, not evidence that Compose is unsuitable.
- Zed is a Rust native editor with its own GPU framework GPUI; the 1.0 release is reported for macOS, Linux and Windows (**secondary**, see Q2).
- Avalonia-based products and AvaloniaEdit users: no verified case studies found (**gap**). Avalonia's Android production apps: none verified (already in `avalonia_clients.md`, Q2).

### Inferences
- The lesson is less about the framework than about who maintains it. Fleet's shutdown and Uno's GTK retirement both show that vendor-led desktop UI stacks can be cut.
- Bazaar's risk profile favours a choice with a maintained open-source core; Avalonia (MIT core) and Monaco (MIT) both qualify, with the Avalonia paid-tier question still open.

### Gaps
- No primary case-study data on IDE-like apps built on Avalonia, MAUI, Uno or Compose.

## Key Question 5. Decision tree and cheap experiments for the Monaco spike

### Takeaway
Run the Windows WebView2 experiment first, because it decides whether Monaco is viable at all on the first head. Android should not wait for Monaco: its own FAQ says it is unsupported on mobile. The owner's contingency only needs a switch if the Windows and Linux WebView experiments both fail.

### Avalonia licence check (blocks any paid route)
- **Verified today:** avaloniaui.net/pricing lists Plus €299/year per seat, Pro €899/year per seat, Enterprise from €21,000/year with three seats, and says "Full source for premium controls" for Enterprise — [Avalonia pricing](https://avaloniaui.net/pricing) (**verified**, fetched 2026-10-25; the same figures are in `avalonia_clients.md`).
- **Conflict:** the same fetched page places a "WebView" entry under a "70+ OSS UI Components" heading, which may mean WebView is free. The Accelerate docs page (secondary) and `avalonia_clients.md` say `NativeWebView` requires Accelerate. A 2026 secondary snippet also says Accelerate is retired, with Plus at $17/month and Pro at $49/month (**secondary**, conflicting with the EUR per-seat page; source: [Avalonia handbook pricing principles](https://avaloniaui.net/handbook/pricing-principles), snippet only). **Unresolved; ask Avalonia directly before spending or relying on NativeWebView.**

### Experiments, in order (cheap first). Pass and fail thresholds marked PROPOSED are this note's suggestions, not sourced.
1. **Windows WebView2 host (free route D: direct HWND host, or `WebView2.Avalonia` for a quick test).** Load a local Monaco bundle over a non-file origin (Monaco workers need http or https). Pass: editor renders, typing works, Korean or Japanese IME composes inside Monaco, focus moves to and from the Vim key layer, theme injection works without reload. Fail: no composition, or focus cannot leave the WebView (PROPOSED).
2. **Linux WebKitGTK host via `Avalonia.WebView` (community, unverified) or `NativeWebView` if the licence allows.** Same tests on X11 (Avalonia's Wayland is opt-in per `avalonia_clients.md`). Pass: same tests. Fail: no focus, no IME, or a crash on resize (PROPOSED).
3. **Android: do not test Monaco for editing.** Test an AvaloniaEdit surface on Android with Gboard and a Korean or Japanese IME, plus the Android WebView with Monaco only as a comparison. Expect Monaco to fail per its FAQ. Pass for the native surface: composition and caret stable on a foldable (PROPOSED).
4. **Editor fallback on Windows: AvaloniaEdit with TextMate.** Measure what is lost: diff, language services, IME. Pass if the owner accepts that diff and LSP must be built separately.
5. **Only then** evaluate a migration. Triggers: experiments 1 and 2 fail, and experiment 4 is rejected. The candidate would be a Tauri-style shell (needs owner approval under the "no web client" rule) or Compose Multiplatform (needs a desktop WebView solution), not a full rewrite.

### Decision tree
- Windows WebView2 Monaco passes (1) → keep Avalonia; Monaco on Windows and Linux (if 2 passes); Android uses a native surface (3). Keep the `IEditorHost` seam (27 Conflicts 1).
- Windows passes but Linux fails (2) → Monaco on Windows only; Linux uses AvaloniaEdit (4) for the editor, or ships Linux later (27 says Linux is lower priority).
- Windows fails (1) → try AvaloniaEdit (4); if rejected, owner decision on a shell (Tauri-style, web UI in a native wrapper) or Compose Multiplatform, given that the server stays .NET.
- Any path that needs a paid Avalonia tier → stop and get the owner's approval and a written answer from Avalonia on NativeWebView licensing.

### Inferences
- The cheapest fallback that keeps the Avalonia codebase is AvaloniaEdit for editing plus a separate diff view. The cheapest one that keeps Monaco is a Windows-only WebView2 host. Neither needs a language change.
- Migrating only the clients keeps the .NET server. The cost is a full client rewrite, so it should be the last option.
- The "no web client" rule (CLAUDE.md) and the "clients render" rule both affect the Tauri option. A native shell with embedded Monaco is not a browser client of the server, but it is a web UI. The owner should decide that explicitly; the rule is not settled by this note.

### Gaps
- No verified Avalonia WebView + Monaco result on any platform.
- Monaco IME behaviour in WebView2 and WebKitGTK unverified.
- Android IME in current Chromium and Android System WebView unverified.
- Accelerate licence and price for NativeWebView unresolved (conflicting sources above).
- Uno, Eto, Qt and Flutter WebView matrices not verified (see above).
- Foldable, headless testing, ONNX Runtime and dictation support for each alternative were not researched.
