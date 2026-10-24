# Bazaar.Windows — Client Framework, IDE, Terminal, Navigation

Part of the Bazaar document family. Siblings: **Bazaar** · **Bazaar.Server** · **Bazaar.Android** · **Bazaar.Linux** · **Bazaar.Soul** · **Bazaar.Constellation**

Status legend: **[DECIDED]** / **[PROPOSED]** / **[REJECTED/CHANGED]** / **[RESEARCHED]** / **[OPEN]** — see Bazaar.md for definitions.

---

## 1. Product Identity

- **[DECIDED]** Philosophy: **"Build products."** Should strive to feel like the best AI IDE. Full app: IDE/text editor, terminal, custom GitHub diff/PR/issue interface, The Constellation, Vault, Kanban, Godot/Roblox side panels, Observatory, company dashboard. This is where genuine *visual* game-engine work happens (see **Bazaar.Soul**, Section 2, for why that's a deliberate split from headless agent VMs, not an oversight).
- **[DECIDED]** Detachable windows: right-click/double-click on anything that opens IDE/text-editor/terminal opens it in its own separate lightweight window, not the full Bazaar shell. Real enough to register as the OS default handler (replacing Notepad / the default terminal), not just an in-app convenience.
- **[DECIDED 2026-10-24, owner]** Start screen: opening the Windows client lands on a **dashboard of what changed** while you were away. Contents **[DECIDED 2026-10-24, owner]**: unread GCs and @mentions; PRs and decisions awaiting you; agents and Kanban (presence, tasks blocked or finished); build health and usage. Order and layout are OPEN.

---

## 2. Client UI Framework — Avalonia (changed from WinUI 3)

- **[DECIDED — CHANGED, major]** One shared .NET/Avalonia codebase across Windows, Android, and Linux, replacing the original "WinUI 3 for Windows + Jetpack Compose for Android, deliberately separate for max native feel" decision. WinUI 3 remains the documented fallback if the shared-code approach doesn't hold up under real use — this was not treated as irreversible. **Context 2026-10-24:** the whole stack is now .NET (server plus all clients), so WinUI 3 stays only as the documented fallback.

### Why the move — logged in full since it reverses a previously-settled decision

- **The deciding factor: headless testing.** `Avalonia.Headless` runs the full UI stack (layout, render, input) to an in-memory bitmap with no window/display required — genuinely equivalent to what Playwright gives web development. WinUI 3 has no equivalent; it fundamentally depends on the Windows compositor (DWM) to render anything. Given how much of Bazaar's premise is agents verifying their own work with minimal human involvement, this is structural, not cosmetic — an agent can write a UI change and get an automated pass/fail on what it actually rendered, for the client apps themselves, the same way it can for backend code.
- **The NPU/ONNX advantage originally credited to WinUI 3 was a mistake, now corrected.** ONNX Runtime is a .NET library capability, available identically under Avalonia — not a UI-framework feature. Removing that pillar made the original WinUI-3 case weaker than it looked at the time.
- **[RESEARCHED]** Terminal and Monaco/WebView2 embedding both verified as real and available under Avalonia, not just theoretical: multiple production-grade ConPTY-based terminal control libraries exist (`Iciclecreek.Avalonia.Terminal` and others — selection, mouse reporting, resizing, standalone or embedded modes all supported), and Avalonia has a documented `NativeWebView` component using WebView2 on Windows. One honest cost: the most polished, officially-documented version of that WebView integration sits under **Avalonia XPF**, a commercial product tier, not the free open-source core — a younger free community alternative exists but is less battle-tested. **[OPEN — claim contradicted by research 2026-10-24]** Avalonia's docs place the official `NativeWebView` under the paid *Accelerate* tier, not XPF (the XPF overview does not mention WebView), and no source shows Monaco running inside any Avalonia WebView. Free community packages exist (WebView2.Avalonia covers Windows and macOS only). Budget Accelerate, accept a community package, or spike first.
- **[RESEARCHED]** For pure terminal fidelity specifically, WinUI 3 has a real edge worth naming honestly: `EasyWindowsTerminalControl` wraps the **actual Windows Terminal team's own rendering code**, not a reimplementation. Avalonia's terminal libraries are excellent third-party recreations, cross-platform, with the same headless-testability property as the rest of the framework — but not byte-for-byte identical to real Windows Terminal the way WinUI 3's option is.
- **The Constellation has a more natural home in Avalonia** — its entire rendering pipeline already *is* Skia (see **Bazaar.Constellation**), the same engine WinUI 3 would need Win2D as a separate interop layer to access.
- **Linux became close to free** — Avalonia draws its own UI rather than wrapping native OS widgets, so a Linux build is closer to "the same app on a different OS" than a real port. This is what unlocked **Bazaar.Linux** on the roadmap at all.
- **[RESEARCHED]** Android's maturity concern was checked and looks resolved — earlier docs described Avalonia-on-Android as experimental; the current release (Avalonia 12) shipped a "completely rebuilt Android backend" targeting reliable high-refresh-rate scheduling and reduced animation jank, with the vendor citing startup (1,960 to 460 ms with NativeAOT) and scrolling (42 to 120 FPS) gains. **[Corrected 2026-10-24]** The announcement does not mention high-refresh scheduling or complex layouts, the vendor numbers have no stated methodology, and Android 16 is Tier 1 while Android 12–15 is Tier 2. A signal, not a substitute for hands-on testing.
- **The honest cost, stated plainly:** Avalonia doesn't use native OS widgets on any platform — it draws everything itself. It won't inherit exact native look/feel/animation/accessibility behavior for free the way WinUI 3 or true native Android would. Not Electron-class overhead (compiled, Skia-based, no bundled browser engine) — but "not bloated" and "pixel-identical to a first-party native app" are different claims, and only the first is well-supported.
- **The practical driver, stated honestly because it's real:** one shared codebase across Server/Windows/Android/Linux is meaningfully cheaper in time and effort for a solo-dev-plus-AI-agents team, especially with less available development time coming once college starts. Not "free" — genuinely free stays off the table regardless — but a real reduction in what has to be built and maintained twice.

---

## 3. Shared UI Code — repo structure

Answering "is that what the `/shared` folder is for?" directly: **not quite — two different kinds of "shared," worth keeping conceptually separate.**

- **`/shared`** (see **Bazaar.Server**) is for **API/data contracts** — the Protobuf schemas defining the WebSocket message format between server and clients. Client↔server communication, not client-to-client code reuse.
- **The Avalonia move needs a new, separate shared UI class library** — the standard Avalonia multi-platform pattern: one core project (e.g. `Bazaar.UI.Core`) holding the actual XAML views, styles, view models, and Bazaar's theme resources, referenced by thin per-platform **"head" projects** (`Bazaar.Windows`, `Bazaar.Android`, `Bazaar.Linux`) containing only what's genuinely platform-specific — ConPTY-vs-Linux-PTY terminal backend, foldable WindowManager bindings, NPU execution-provider selection (OpenVINO on Intel through Windows ML, QNN on Qualcomm; DirectML is legacy GPU only), file-association/installer registration. Worth naming distinctly in the repo (e.g. `/client/shared-ui` alongside `/shared`) so the two aren't confused later.

---

## 4. Editor, Terminal, Vim Navigation

- **[DECIDED]** **Editor**: Monaco, embedded in a scoped WebView2 island — `CoreWebView2Environment` pre-warmed at app startup (kills cold-start lag), composition-based embedding via `CoreWebView2CompositionController` (not the basic HWND control) so it visually composites with native chrome instead of feeling glued on, WebView2's own context menu/zoom/chrome stripped entirely, custom Monaco theme JSON matching Bazaar's palette exactly. Same bundle + theme used for the standalone detached editor window, so it stays recognizably "Bazaar" even outside the main shell. Also reused for the GitHub diff viewer via Monaco's built-in diff-editor mode (see **Bazaar.Server**).
- **[DECIDED]** **Terminal**: a real ConPTY-based terminal control (library TBD between Avalonia's community options — see Section 2), both embedded and standalone/detached, targeting registration as the OS default terminal handler.
- **[DECIDED]** Full Vim-style modal command navigation — nothing focused by default; typed mnemonic commands rather than `Ctrl+#` shortcuts (`w` → window menu, `wc` → focus chat, `wi` → focus IDE), leader-key driven (`<Space>`), `i` for insert mode, `Esc` to return to command mode. Chosen for being more "riffable"/phonetic and for **stenography compatibility**. Mouse click-to-focus still works alongside it. **[DECIDED 2026-10-24, owner]** A fuzzy command palette is added, opened by a mnemonic rather than a Ctrl chord; standard Ctrl shortcuts stay out.
- **[DECIDED]** Collapsed/expandable action logs (Claude-Code-style "Read 15 files" chips) — generated locally via the platform-appropriate NPU, never pre-summarized server-side.
- **[DECIDED 2026-10-24, owner]** Theming: **fully themeable.** One user-editable, shareable palette/theme file drives both the Avalonia styles and the Monaco theme JSON (generated from it). Format is OPEN.
- **[DECIDED 2026-10-24, owner]** GC message anatomy (shared UI, applies to all clients): avatar, name and role badge; model badge and cost chip; presence dot and timestamp (with date separators); plus Discord-style profile cards, custom status and activity (what the agent is doing right now). Collapsed action chips as above. Layout details are OPEN.

---

## Open Questions (Windows)

1. **Avalonia hands-on validation spike** — the framework decision is logged, but the docking/animation "feel" comparison against WinUI 3 was recommended, not yet performed. Treat the decision as strong-but-not-fully-derisked until that happens.
2. **Avalonia XPF (paid) vs. free community WebView2 package** — which one Monaco embedding actually uses isn't decided; real cost/maturity tradeoff either way.
3. **Terminal control library choice** — several viable Avalonia options identified, not narrowed down to one.
4. **Avalonia licence tiers** — Plus (EUR 299/seat/yr) and Pro (EUR 899/seat/yr) charge per seat, with no stated open-source exemption; confirm Accelerate and XPF terms with Avalonia and counsel before referencing them from the public repo.
