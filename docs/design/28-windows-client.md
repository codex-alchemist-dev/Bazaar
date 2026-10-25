# Bazaar.Windows client: IDE, terminal, PR UI, default apps

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 27-client-architecture.md, 02-wire-protocol-and-sync.md, 15-security-taint-and-approvals.md, 20-engine-integration-api.md, 21-github-integration.md, 30-linux-client-and-packaging.md, 31-local-coworker-npu.md · Used by: 26-fun-systems.md, 29-android-client.md, 32-remote-access-auth-and-privacy.md, 34-testing-strategy.md, 35-spikes-plan.md

## Purpose
The Windows head of the Avalonia client. It is where the IDE, terminal, PR and diff UI, the desktop-only Roblox Studio bridge and the local NPU run for the human. It owns OS integration (detached windows, file and terminal handlers, installer) and holds no durable state of its own beyond a cache.

## Decided inputs
- Philosophy "Build products"; full scope: IDE, terminal, custom GitHub diff/PR/issue UI, Constellation, Vault, Kanban, Godot and Roblox panels, Observatory, dashboard. (Bazaar.Windows §1)
- Monaco in a WebView2 island: pre-warmed environment, composition embedding, stripped chrome, theme generated from the user's theme file. Not a web client. (§4, owner 2026-10-24)
- ConPTY terminal, embedded and standalone. Library OPEN. (§4)
- Detachable windows. Registers as a handler for text/code files and as a terminal; guides the user to Windows Settings. Never claims a silent default. (§1, CHANGED)
- Start screen: what changed while away (unread GCs and @mentions; PRs and decisions awaiting you; agents and Kanban; build health and usage). (§1)
- Vim-style modal navigation, leader key, mnemonics, fuzzy palette, no Ctrl chords. (§4)
- Roblox Studio runs only on this desktop. (Bazaar.Soul §2) Clients render; the server computes. No web client. (CLAUDE.md)
- Local NPU for collapsed action-log summaries; never server-side summaries. (§4)
- One theme file drives Avalonia and Monaco; format OPEN. (§4)

## Design
**Process model [PROPOSED].** One client process, many top-level windows (main shell, detached IDE, terminal and diff windows). All share one WebView2 environment and one Monaco bundle. One WebView2 controller per window, not per tab; tabs swap Monaco models over a JS bridge.

**Launcher [PROPOSED].** `--open <path>` and `--terminal` forward to the running instance over a per-user named pipe. A new instance starts only if no instance answers. A named mutex enforces a single instance.

**Startup budgets [PROPOSED, spike targets, not measured].**

| Path | Target |
|---|---|
| Launcher to shell window visible (warm) | 1.0 s |
| Dashboard from local cache | 0.5 s; from server on LAN 2 s |
| Pre-warmed editor to first file rendered | 300 ms |
| Detached window opened | 500 ms |

**Monaco hosting [OPEN, spike].**

| Option | Licence | Platforms | Status |
|---|---|---|---|
| A. Avalonia `NativeWebView` (WebView2 on Windows) | Accelerate, paid (price not found) | Win, mac, Linux, Android | Documented; no source shows Monaco in it |
| B. `WebView2.Avalonia` | Community; licence unverified | Win, mac | No Linux or Android |
| C. `Avalonia.WebView` (community) | Unverified | Win, Linux, Android | Maturity unverified |
| D. Direct WebView2 HWND host | WebView2 SDK terms (needs check) | Windows only | Full control; we write the interop |

The decided composition embedding (`CoreWebView2CompositionController`) is reachable from none of A to C as far as the sources show. Spike pass criteria: composition or HWND embedding; keyboard and focus handoff to the Vim layer; IME in Monaco; memory per window; Monaco ready time with a pre-warmed environment; theme injection without reload.

**Editor, diff and PR panes [PROPOSED].** Editor: Monaco per window. Diffs: Monaco diff mode. PR and issue lists, reviews and comments: native Avalonia. PR body Markdown: rendered in the WebView, not with a Pro-tier control (see Conflicts). The client holds no GitHub credential; all GitHub reads and writes go through the server (21).

**Desktop Studio bridge [PROPOSED].** The client is an MCP client of Roblox's built-in Studio server over local stdio, when Studio is open. The server (15, 20) issues a grant per call. The client runs only calls that carry a valid grant, returns the result, and keeps no grants and no audit log. Write tools need owner approval on the server before relay. Studio's server has no authentication, so only this client process connects.

**Playtest support [PROPOSED].** Console output, state dumps and scripted input through the bridge. Periodic `screen_capture` only at a rate the owner sets, because screenshots are costly and agents cannot watch a viewport (Soul §2). Godot runs its GUI here only when a human needs it.

**Local NPU [PROPOSED].** Probe at start: OpenVINO EP on Intel NPU (Core Ultra Series 1+) via Windows ML, then ORT CPU. QNN later. Pin the NPU driver to 32.0.100.5540 or newer; avoid 32.0.100.4300, .4509 and .4512 (crash reports). Cache compiled models keyed on driver plus OpenVINO version. Show the active backend in the UI, and check that ops ran on the NPU rather than falling back silently to CPU. No tokens-per-second figure is sourced; benchmark before any budget.

**Start screen [PROPOSED layout].** Sections in order: unread and mentions; awaiting you; agents and Kanban; build health and usage. Fed by one server "since last seen" query (02).

**OS integration [PROPOSED].** The installer registers per-user ProgIDs and capabilities (HKCU). It never writes UserChoice. An in-app "Make Bazaar default" action opens Windows Settings default apps and explains the one step. Terminal: implement the Windows Terminal console handoff only if its spike passes; otherwise Bazaar is a terminal the user opens, not the default.

**Installer [PROPOSED].** Velopack per-user install, GitHub Releases feed, delta updates. No service. Signing per Question 11. WebView2 Runtime checked at first run (redistribution terms need a check).

**Vim layer [PROPOSED].** A preview-key handler at the window root. The key-state machine lives in Client.Core so it can be unit tested without a window. Any focused text input is insert mode, so IME composition is never intercepted.

## Data and interfaces
```csharp
public interface IMonacoHost : IAsyncDisposable
{
    Task OpenModelAsync(ModelRef model, CancellationToken ct);   // file or diff
    Task ApplyThemeAsync(EditorTheme theme, CancellationToken ct);
    event EventHandler<EditorEvent> Events;                      // edit, focus, save
}
public interface ITerminalHost { Task AttachAsync(TerminalSpec spec, CancellationToken ct); }  // ConPTY behind it
public sealed record StudioRelayRequest(string GrantId, string StudioId, string Tool, JsonElement Args);
public sealed record StudioRelayResult(string GrantId, bool Ok, JsonElement Payload);
```
Contracts: 02 supplies GC and since-last-seen data; 21 PR and diff data; 20 and 15 Studio grants; 27 palette, theme tokens and key router.

## Behaviour and edge cases
- **Renderer crash:** reload the Monaco bundle and reopen models from the server. Unsaved edits stay in a local draft file until the server accepts them.
- **Offline:** dashboard shows cache age; editor goes read-only; reconnect resumes from the sequence number (02).
- **Second launch:** forwarded to the running instance. If it does not answer within 2 s, the launcher starts a new instance and warns.
- **Terminal:** one ConPTY per session. Closing a window with running processes asks first.
- **Studio relay:** refused when no Studio is open or the StudioId is unknown. The client allow-list must match the server's.
- **IME:** composition in Monaco and the terminal must work in insert mode (tested on a real desktop).

## Testing
- **Avalonia.Headless:** start-screen layout at three widths; palette ranking; mnemonic routing; detached-window model; theme resources. WebView and terminal render as placeholders.
- **Unit tests (no UI):** Vim key-state machine; theme-to-Monaco generator (golden JSON); pipe parser; relay grant check.
- **Monaco bundle:** a headless Chromium harness with a fake host bridge. Test-only, not a client [PROPOSED].
- **WebView2 and ConPTY:** a Windows CI runner with a fixture shell. Not headless.
- **Handler registration:** a throwaway Windows user account checks ProgID and capability keys. The Settings step is a manual checklist.
- **Studio:** a fake stdio MCP server with recorded responses. No live Studio in CI.
- **Manual desktop pass:** IME in Monaco and terminal; DPI at 100, 150 and 200 percent; detached-window focus; default-app step.

## Work packages
1. Launcher and single-instance named-pipe IPC (needs 27 shell).
2. Theme-to-Monaco generator and Avalonia resource mapping (needs theme format, Q6).
3. Monaco bundle build and JS bridge, with harness test.
4. WebView host spike across options A to D, then the host (needs 3).
5. Editor pane with multi-model tabs (needs 4, 02).
6. Diff and PR/issue panes (needs 5, 21).
7. Terminal spike over candidates, then ConPTY pane (needs 1 for detached windows).
8. Default-terminal handoff spike (independent).
9. Vim key-state machine and command palette (needs 27).
10. Start-screen dashboard (needs 02 query, 27).
11. Handler registration and "Make default" step (needs 1, 12).
12. Velopack installer and WebView2 check (needs 30).
13. NPU probe and Windows ML/OpenVINO wrapper (needs 31).
14. Studio relay client (needs 20, 15).
15. Playtest console and state relay (needs 14, 26).
16. Windows CI lane for ConPTY and WebView2 tests (needs 34).

Packages 1, 3, 7, 8, 9 and 13 can run in parallel.

## Conflicts
1. **Composition embedding** (decided) has no verified Avalonia path. The spike may force an HWND-hosted WebView2 with overlay compositing.
2. **Monaco in an Avalonia WebView** is unverified. The earlier "verified" wording is withdrawn; the spike decides.
3. **"XPF has the best WebView"** is contradicted: the official `NativeWebView` sits under Accelerate. Accelerate's price was not found. Plus (EUR 299 per seat per year) and Pro (EUR 899 per seat per year) have no open-source exemption. **Needs counsel.**
4. **Pro-tier controls:** Markdown Viewer and Rich Text Editor would hit PR bodies and chat. The seat rule counts developers using them at any moment.
5. **Terminal:** the doc calls the Avalonia library production-grade and claims a fidelity edge. No candidate is verified. The real Windows Terminal rendering code (WinUI's `EasyWindowsTerminalControl`) is lost under Avalonia.
6. **Headless testing** is not "equivalent to Playwright." It cannot test WebView, ConPTY, IME, window management or handler registration. Doc 34 needs a manual desktop layer.
7. **Replacing Notepad** (DECIDED) happens only when the user chooses it in Settings.
8. **NPU:** "DirectML vs QNN" is wrong for Intel; the NPU path is OpenVINO through Windows ML. Windows speech-to-text is not researched.
9. **Signing:** SignPath requires OSI-only components, so Avalonia paid tiers or `pg_search` in a signed build would block it. `cicd_repo_licensing.md` keeps the certificate as a private-repo secret; SignPath holds keys itself. Pick one model.
10. **Roblox:** Studio's MCP server has no authentication and lets clients modify open places. Creator and Open Cloud terms are unchecked. **Needs counsel.**
11. **Velopack** does not cover code signing or Windows Services and installs per user only.

## Questions for the owner
1. **Monaco hosting:** approve the four-option spike. Buy Accelerate only after its price is quoted and fits. Default: no purchase until quoted.
2. **Composition fallback:** if composition is unreachable, accept an HWND-hosted WebView2 with overlay? Default: yes.
3. **Terminal:** spike Iciclecreek.Avalonia.Terminal and Terminal.Avalonia, with a custom xterm build over ConPTY as fallback? Default: yes.
4. **Process model:** one process (default), or terminals in a separate host so a GUI crash does not kill shells? Default: one process; revisit after the spike.
5. **Startup budgets:** adopt the table as spike targets? Default: yes.
6. **Theme file:** versioned JSON with named tokens, read by Android and Linux too? Default: yes.
7. **File types to register:** default text and code extensions (.txt, .md, .json, .yaml, .cs, .py, .ts, .js, .gd, .lua) plus terminal. Confirm the list.
8. **Local files:** does the IDE edit files on the human's disk, or only through the server? Default: server-backed; local folders read-only until a save is accepted.
9. **Windows dictation:** in v1 on desktop? Default: no; research Windows speech APIs first.
10. **Desktop NPU:** Intel only for v1? Default: yes; Snapdragon later.
11. **Signing route:** SignPath if the signed build has no commercial components, else Azure Artifact Signing, else an OV certificate. Default as stated; counsel confirms eligibility and custody.
12. **Avalonia paid tiers:** no Plus or Pro in v1; Accelerate only if Question 1 passes. Confirm.
13. **Studio writes:** every write needs owner approval until doc 15 has trust rules. Confirm.
14. **Playtest capture:** scheduled screenshots, owner-reviewable, at a rate set with doc 23's cost model. Confirm the rate and the `<Space>` leader key with the mnemonic list.
