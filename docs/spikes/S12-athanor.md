# Spike S12 (Athanor): Monaco in WebView2 inside an Avalonia 12 window

Branch: `agent/athanor-spikes`. Prototype: `spikes/S12/` (throwaway, not in `src/`). Design source: `docs/design/57-athanor-ide.md` sections 2, 3, 4 and 10; `docs/design/28-windows-client.md`. Runbook: `docs/spikes/runbooks/S12.md` (the criteria below follow the brief for this run and the runbook where it overlaps).
Run: 2026-11-20, automated by the prototype itself (`S12.exe --auto`). Linux counterpart: `docs/spikes/S13-webkitgtk.md`.

## Verdict

**Windows: PASS** on every automated criterion in 3 of 3 runs. Two criteria are only simulated (IME, and a real Ctrl+V keystroke on a real keyboard) and need the owner's hands before this counts as the runbook's feel sign-off. One design finding: with Avalonia's `NativeControlHost` the WebView2 is hosted as a **child HWND (the "HWND route", doc 28 option D), not as a composition-embedded visual**. It works, but it carries the usual airspace limits (see Findings).

## Method

| Item | Value |
|---|---|
| Windows | 11 Pro 10.0.26300 |
| WebView2 Runtime | 157.0.4341.60 (Evergreen) |
| WebView2 SDK | `Microsoft.Web.WebView2` 1.0.4314.35 (Core only, no WinForms/WPF controls) |
| Avalonia | 12.1.6 (the version pinned in `Directory.Packages.props`) |
| .NET | 11.0.100, `net11.0-windows` |
| Monaco | 0.57.0, `min/vs` build from the npm tarball, sha256 `3ea1712fbacd3290cf4751007e3a5b57cc279767607812d4c3e57925fb0b05c2` (checked by `fetch-monaco.ps1`), licence MIT (the package LICENSE is copied to `web/monaco/LICENSE`) |
| Host | Intel Core Ultra 9 285K, Intel Arc B580, 96 dpi (100%). Other agents were using the machine, so timings are noisy |
| Hosting | `NativeControlHost` creates a child HWND; `CoreWebView2Controller` is parented to it; bounds follow `ArrangeOverride` |
| Origin | `SetVirtualHostNameToFolderMapping("athanor.invalid", web/)`, so the page is `https://athanor.invalid/host.html`. Workers load (a `file://` origin does not allow them). No loopback server, no socket opened |
| "Offline" | The network adapter was **not** disabled (that is a system setting). Instead the WebView2 runs with a dead proxy (`--proxy-server=http://127.0.0.1:9`) and every request is logged through `WebResourceRequested`. Result: 0 requests left the virtual host |
| Bundle | Not committed (about 25 MB). `spikes/S12/fetch-monaco.ps1` vendors the pinned tarball into `web/monaco/`. Only that one version is referenced |
| Theme | `dark.monaco.json` and `light.monaco.json`, the golden outputs of `MonacoThemeGenerator` (`tests/Bazaar.Client.Theme.Tests/Golden`). The spike copies the goldens instead of referencing the project |
| Bridge | Doc 57 section 4: JSON `{type, seq, documentId?, ...}`. Host to editor via `PostWebMessageAsJson`, editor to host via `chrome.webview.postMessage`. The same page script is reused by S13 |

## Results (per criterion)

| # | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | Load a local Monaco bundle | PASS 3/3 | Editor ready in every run; a web worker is created from the virtual host origin; 0 external requests |
| 2 | Bridge, host to editor (`open`, `setTheme`, `applyEdit`, `focus`, `setClaimedKeys`, `showDiff`) | PASS 3/3 | all applied |
| 3 | Bridge, editor to host (`ready`, `changed`, `selection`, `keyPressed`, `error`) | PASS 3/3 | received and decoded |
| 4 | `seq` guard both ways | PASS 3/3 | the editor rejected a host message with a stale `seq` (answered with an `error`); the host rejected an editor message with a stale `seq` (counted, dropped). An unknown `type` is rejected with an `error` |
| 5 | Theme from `MonacoThemeGenerator` | PASS 3/3 | editor background `rgb(24,24,37)` = `#181825`; swapped to the light theme and back **without reload**, background changed live |
| 6 | Key handoff, no Ctrl chords claimed | PASS 3/3 | The host pushes a claimed-key list (`Alt+KeyM`, `F6`). A real key event (CDP) for Alt+M is swallowed by Monaco, `keyPressed` is sent, and the host moved Avalonia focus to a native `TextBox` (`IsFocused` true). Ctrl+S (unclaimed) stayed inside Monaco, no `keyPressed`. The claimed list contains no Ctrl entry |
| 7 | Diff editor | PASS 3/3 | `.monaco-diff-editor` present and `onDidUpdateDiff` reported 2 line changes |
| 8 | Typing (ASCII) | PASS 3/3 | 20 characters via trusted CDP key events, 0 mismatches. A real keyboard was not exercised |
| 9 | IME composition | **SIMULATED PASS, owner check needed** | CDP `Input.imeSetComposition` plus `insertText` commits the text, and a cancelled composition leaves nothing. This does not prove a real Windows IME (TSF) works through a child-HWND WebView2; runbook step 6 stays open |
| 10 | Paste (non-ASCII) | PASS 3/3 | Avalonia clipboard text `paste: ünï©ödé 日本語 🚀` pasted into Monaco with the Ctrl+V editing command; equal to the clipboard. A real Ctrl+V by the owner is not done |
| 11 | Renderer crash handling | not exercised | `ProcessFailed` is logged; zero crashes in 3 runs |

No criterion failed in any run. Raw output: `spikes/S12/results/win-run{1,2,3}.json`.

## Numbers (3 runs, cold app start each, milliseconds since process start)

| Run | WebView2 env and controller created | Monaco `ready` | First paint (2x rAF after editor create) | Page script start to ready |
|---|---|---|---|---|
| 1 | 3208 | 3490 | 3491 | 242 |
| 2 | 1219 | 1461 | 1462 | 202 |
| 3 | 1830 | 2049 | 2050 | 146 |

- Median cold start to first paint: **2.05 s** (range 1.46 to 3.49 s). Almost all of it is WebView2 environment creation (1.2 to 3.2 s); Monaco itself needs 150 to 250 ms once the controller exists. This argues for the doc 28 environment pre-warm (create the environment at client start, before a window needs it). A warm second window should be about 0.2 to 0.3 s; not measured.
- "First paint" is a `requestAnimationFrame` after editor creation, not a compositor-level paint timestamp. Treat it as a lower bound within about one frame.
- Memory, after opening a document and the diff editor (private bytes): Avalonia process **94 to 95 MB**; WebView2 processes **255 to 259 MB** (browser 38, GPU about 99, renderer about 99, two utility 7 and 11). Total about **350 MB** for one editor window. One shared environment for all windows (doc 28) is what keeps N windows from costing N times this.

## Findings and risks

1. **HWND route, not composition.** `NativeControlHost` is Avalonia's supported way to embed a native control and it yields a child HWND. That is doc 28 option D. The S12 runbook calls composition the first design; Avalonia 12 offers no free WebView2 composition visual (`NativeWebView` is the paid Accelerate path, excluded). Consequence: Avalonia content cannot draw on top of the editor (airspace), popups and tooltips over the editor must be separate windows or the editor must be inset, and animations that cross the editor do not composite. Doc 28 and doc 57 section 1 should say "child HWND via NativeControlHost" unless the owner wants a deeper composition spike (`CreateCoreWebView2CompositionController` fed into an Avalonia composition visual).
2. **Focus.** Keyboard focus sits inside the WebView2 HWND; Avalonia's key router never sees keys Monaco consumes. The claim-list design (doc 57 section 7) works: Monaco decides synchronously from a pushed list and posts `keyPressed`; the host then takes focus. A key not on the list never reaches the router, so the list must contain every mnemonic and pane switch, and must be pushed again whenever the key map changes.
3. **IME and a real keyboard are unverified** (rows 8 and 9). Needs the owner on a real machine with their input method. The CDP route does not go through TSF.
4. **Licensing (for counsel, not decided here).** Monaco is MIT. The `Microsoft.Web.WebView2` NuGet package ships a BSD-style licence file (read in the package). The WebView2 Evergreen **Runtime** is Microsoft software installed separately under Microsoft's terms; redistribution terms need counsel for the OSI-only public build (doc 28 Conflicts 3).
5. **Bundle size.** The full `min/vs` tree is 25 MB (languages, workers, codicons). `Bazaar.Client.Athanor.Bundle` should ship a trimmed bundle. Not done in the spike.
6. **Spike-only test hooks** in the bridge page (`getText`, `testStaleOut`, `__probe`, `__evalReply`) must not move into the production bridge.
7. The page never builds a script string from user text: on Windows the host posts JSON through `PostWebMessageAsJson`, as doc 57 section 4 requires.

## Reproduce

```
cd spikes/S12
powershell -File fetch-monaco.ps1      # vendors Monaco 0.57.0 into web/monaco (not committed)
dotnet build -c Release
bin/Release/net11.0-windows/S12.exe --auto --out results/win-run1.json
```

Without `--auto` the window stays open for manual typing, IME and paste checks.

## Consequence for doc 57 section 10

- New open questions: composition versus child-HWND hosting (owner), and the trimmed-bundle task.
- Doc 57 section 4 holds as written (seq guard, type rejection, no script strings from user text); no change needed.
- S12 stays open for the IME and real-keyboard sign-off; do not mark the runbook's feel criteria passed from this record.
