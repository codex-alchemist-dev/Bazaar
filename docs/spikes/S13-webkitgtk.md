# Spike S13 (Athanor): Monaco in WebKitGTK from .NET

Branch: `agent/athanor-spikes`. Prototype: `spikes/S13/` (throwaway). Design source: `docs/design/57-athanor-ide.md`, doc 27 Q9 (Linux editor surface), `docs/spikes/S12.md`. Run: 2026-11-20 in WSL2 Ubuntu 24.04 (distro stored on V:), automated by the prototype (`S13.dll --auto`) under Xvfb with the X11 backend.

## Verdict

**WebKitGTK itself: PASS.** Monaco loads from a bundle, and the bridge, theme, key handoff, diff, typing and paste all work in 3 of 3 runs. **Inside an Avalonia window: NOT PROVEN**, and not available for free on the display servers that matter. The spike is a plain GTK host driven from .NET over P/Invoke; Avalonia was not part of the loop. IME is **not tested** (no input-method framework in this session).

## Method

| Item | Value |
|---|---|
| OS | Ubuntu 24.04.5 LTS under WSL2, kernel 6.18.58.1-microsoft-standard-WSL2 |
| WebKitGTK | 2.52.6 (`libwebkit2gtk-4.1-0` 2.52.6-0ubuntu0.24.04.2), GTK 3.24.41 |
| .NET | 11.0.100 (installed to `/opt/dotnet11` inside the distro) |
| Display | `xvfb-run` with `GDK_BACKEND=x11`, 1280x800x24; software rendering (no DRI3). WSLg (Wayland) is present but not used, because XTEST key injection needs X11 |
| Host | C# console app, no Avalonia: `gtk_init`, `GtkWindow` > `GtkBox` > `GtkEntry` + `WebKitWebView`, all via P/Invoke (`libgtk-3`, `libgobject-2.0`, `libwebkit2gtk-4.1`, `libjavascriptcoregtk-4.1`, `libgio-2.0`). No wrapper package, so no extra licence |
| Monaco | 0.57.0, same bundle, theme goldens and page as S12 (shared `spikes/S12/web`) |
| Origin | custom scheme `athanor://app/` registered secure and CORS-enabled, files served from disk by a `WebKitURISchemeRequest` handler. A `Worker` created from this origin works. No sockets |
| Bridge | Same JSON and `seq` rules. JS to host: `webkit.messageHandlers.bridge.postMessage(json)` raising `script-message-received::bridge`. Host to JS: `webkit_web_view_evaluate_javascript("window.__fromHost(<json>)")`, where the JSON literal comes from the serializer and no user text is concatenated into script. The page script has a transport shim, so one file runs on WebView2 and WebKitGTK |
| Input | real X11 events through `xdotool` (XTEST) for typing, Alt+M and Ctrl+S; GTK clipboard plus `webkit_web_view_execute_editing_command("Paste")` for paste |
| Sandbox | The WSL session runs as root, so `WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1` was set. A real deployment must run with the WebKit sandbox on |

## Results

| # | Criterion | Result | Evidence |
|---|---|---|---|
| 1 | Load local Monaco bundle, offline | PASS 3/3 | ready every run; worker from `athanor://` OK; every asset resolved from disk (a missing-file list stayed empty). No network was configured or used, but this prototype has no request log like WebView2's, so "no external request" is by construction, not measured |
| 2 | Bridge both ways, `seq` guard, unknown type | PASS 3/3 | stale host `seq` rejected by the editor, stale editor `seq` rejected by the host, bogus type rejected |
| 3 | Theme (`MonacoThemeGenerator` golden) | PASS 3/3 | background `rgb(24,24,37)`; live swap to light and back, no reload |
| 4 | Key handoff, no Ctrl chords | PASS 3/3 | a real Alt+M event was swallowed by Monaco, `keyPressed` was sent, `gtk_widget_grab_focus(entry)` moved focus (`has_focus` true); Ctrl+S stayed in Monaco |
| 5 | Diff editor | PASS 3/3 | diff DOM present, 2 line changes |
| 6 | Typing ASCII | PASS 3/3 | 20 characters through XTEST, 0 mismatches. WebKit under software rendering is slow: the first attempt read the text early and saw a partial string, so the harness now waits for the text to settle |
| 7 | Paste non-ASCII | PASS 3/3 | the `Paste` editing command yields the exact clipboard text. In the first attempt a raw Ctrl+V through XTEST arrived together with leftover typing from criterion 6; that was the same timing artefact |
| 8 | IME | **NOT TESTED** | no ibus or fcitx in Xvfb or the WSLg session. Needs a real Linux desktop |

Raw output: `spikes/S13/results/linux-run{1,2,3}.json`.

## Numbers (3 runs, milliseconds since process start, includes .NET startup)

| Run | Monaco `ready` | First paint | Page script start to ready |
|---|---|---|---|
| 1 | 1263 | 1300 | 382 |
| 2 | 1722 | 1804 | 484 |
| 3 | 2191 | 2371 | 964 |

Median to first paint **1.8 s**, under Xvfb software rendering with other load on the machine, so it is not one-for-one comparable with S12. Memory (PSS, one window, diff editor open): host process 108 to 112 MB (a bare .NET process, **not** an Avalonia head), `WebKitWebProcess` 352 to 365 MB, `WebKitNetworkProcess` about 20 MB. Total about **490 MB**, roughly 140 MB more than the Windows total, mostly the web process (software rendering, no GPU process).

## Can this live inside Avalonia? (analysis, not proven)

- **Avalonia on X11:** `NativeControlHost` accepts an X11 window id (XID) and re-parents it. A GTK widget has no XID of its own to hand over, so this needs `GtkPlug` and `GtkSocket` (XEmbed) or a top-level GTK window re-parented under the Avalonia window. Both are fragile (focus, resize, DPI), and GTK3 plugs are a legacy path.
- **Event loops:** GLib and Avalonia each want to own the main-loop thread. One thread means pumping one from the other; two threads means X11 thread-safety care. Not attempted.
- **Wayland:** XEmbed and re-parenting do not exist, and Avalonia's `NativeControlHost` has no Wayland implementation. Current Ubuntu defaults to Wayland, so an Avalonia-embedded WebKitGTK editor would force an XWayland session.
- **Free wrapper packages:** the community Avalonia WebView wrappers were not evaluated for licence or Avalonia 12 support. Avalonia's own `NativeWebView` is the Accelerate path (excluded).
- **Options for the Linux editor (owner decision, doc 27 Q9):** (a) AvaloniaEdit as the native Linux editor (MIT, no WebView); (b) a separate GTK helper process hosting Monaco in WebKitGTK as its own top-level window, talking to the client over the same JSON bridge on a local pipe; (c) an XEmbed spike behind an X11-only requirement. The doc 57 section 4 bridge is transport-neutral, so (a) and (b) both fit behind `IEditorHost`. This spike supports (b) as technically sound and (a) as the lower risk.

## Risks

1. Not Avalonia-embedded; embedding is unsolved (above).
2. IME untested on Linux; WebKitGTK relies on GTK input-method modules, so behaviour depends on ibus, fcitx5 and Wayland text-input.
3. Software-rendering numbers only; GPU behaviour in a real session not measured.
4. The P/Invoke surface is hand-written and untyped; a GTK or WebKit major change breaks it silently. A real module would use a generated binding (licence check needed) or stay at the process boundary in option (b).
5. The sandbox was disabled for the test (root). Production must not do that.
6. The WebKitGTK version is a distribution decision (Ubuntu 24.04 ships 2.52 here); a minimum supported version should be pinned.

## Reproduce

```
# inside WSL Ubuntu 24.04: apt install libwebkit2gtk-4.1-0 xvfb xdotool; .NET 11 SDK at /opt/dotnet11
cd spikes/S12 && powershell -File fetch-monaco.ps1   # on Windows, once, to create web/monaco
bash spikes/S13/run.sh results/linux-run1.json
```
