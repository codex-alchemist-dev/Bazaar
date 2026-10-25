# Bazaar.Windows client: IDE, terminal, PR UI, default apps

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 10
Depends on: 02-wire-protocol-and-sync.md, 12-kanban-and-dependencies.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 20-engine-integration-api.md, 21-github-integration.md, 27-client-architecture.md, 30-linux-client-and-packaging.md, 31-local-coworker-npu.md · Used by: 26-fun-systems.md, 29-android-client.md, 32-remote-access-auth-and-privacy.md, 34-testing-strategy.md, 35-spikes-plan.md

## Purpose
The Windows head of the Avalonia client. It hosts the IDE, the human's local terminal, the PR and diff UI, the desktop-only Roblox Studio bridge and the local NPU run. It owns OS integration (detached windows, file and terminal handlers, installer) and holds no durable state beyond a cache. Agent terminals run inside the agents' VMs and are streamed by the server (16, 17); this client does not host them.

## Decided inputs
- Philosophy "Build products"; full scope: IDE, terminal, custom GitHub diff/PR/issue UI, Constellation, Vault, Kanban, Godot and Roblox panels, Observatory, dashboard. (Bazaar.Windows §1)
- Clients render; the server computes. Ratified client-side exceptions (DECISIONS-LOG round 4, C17): NPU inference, the human's local terminal, the Roblox Studio bridge relay, and syncing the viewed branch to a local folder. Layout stays server-side.
- Local terminal: a built-in replacement for Windows Terminal running locally (DECISIONS-LOG round 4, C31). Agent terminals are server-side (doc 17). ConPTY behind it; library OPEN.
- Monaco in a WebView2 island, with composition embedding as the first design (owner, 2026-10-24). No verified Avalonia path exists for composition (C01). Monaco hosting is a GATE for the Avalonia decision as a whole; the spike uses free, OSI-compatible routes only (DECISIONS-LOG round 5, C26).
- OSI-only signed build: no paid Avalonia components, Postgres full-text search on Windows, SignPath as the signing route (DECISIONS-LOG round 4, C23). No Accelerate, Plus or Pro controls in the public build.
- Detachable windows. Registers as a handler for text and code files and as a terminal; guides the user to Windows Settings; never claims a silent default. Replacing Notepad happens only when the user chooses it (§1, CHANGED; C07).
- Start screen: what changed while away (unread GCs and @mentions; PRs and decisions awaiting you; agents and Kanban; build health and usage). (§1)
- Vim-style modal navigation, leader key, mnemonics, fuzzy palette, no Ctrl chords. (§4)
- Roblox Studio runs only on this desktop. (Bazaar.Soul §2; CLAUDE.md)
- Local NPU for collapsed action-log summaries; never server-side summaries. (§4; C17)
- One theme file drives Avalonia and Monaco; format OPEN. (§4)
- Owner merges from the Bazaar clients (DECISIONS-LOG round 8, C25): PR management and merge happen in the app; the server performs the squash merge on the owner's signed approval. The merge credential is a dedicated owner GitHub App or the owner's linked GitHub credential, chosen in the setup wizard (round 12). For a card tied to a PR, Done means merged; reviewer approval is a flag (round 8, C28).

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

**Monaco hosting [OPEN, gate spike].**

| Option | Licence | Platforms | Status |
|---|---|---|---|
| A. Avalonia `NativeWebView` | Accelerate, paid | Win, mac, Linux, Android | Excluded: paid, not in the OSI-only build (round 4, round 5). Not spiked. |
| B. `WebView2.Avalonia` | Community; licence unverified | Win, mac | Spike only if the licence is verified OSI. No Linux or Android. |
| C. `Avalonia.WebView` | Community; unverified | Win, Linux, Android | Spike only if free and OSI. Maturity unverified. |
| D. Direct WebView2 HWND host | WebView2 SDK terms (OSI fit unchecked) | Windows only | Accepted fallback (round 4). Full control; we write the interop. |

Spike pass criteria: composition or HWND embedding; keyboard and focus handoff to the Vim layer; IME in Monaco; memory per window; Monaco ready time with a pre-warmed environment; theme injection without reload. If no free route passes, the project may move off Avalonia to another open-source platform, with the owner's accepted migration (round 5). The contingency survey (`docs/research/notes/Bazaar UI framework contingency/ui_framework_alternatives.md`) recommends the order: Windows WebView2 Monaco, then Linux WebKitGTK, then a native Android surface, then AvaloniaEdit as the native fallback. Monaco is not used on Android (CLAUDE.md).

**Editor, diff and PR panes [PROPOSED].** The editor is Monaco per window behind `IEditorHost`, so a native fallback can replace it. Diffs use Monaco diff mode. PR and issue lists, reviews and comments are native Avalonia. PR body Markdown renders in the WebView (no paid control). **In-app review and merge:** the owner reviews in the client; the client sends a signed approval; the server merges (squash, with the `Signed-off-by` line from CLAUDE.md) and records the platform's signed human identity (17, 21). The client holds no GitHub credential; all GitHub reads and writes go through the server (21, 14).

**Desktop Studio bridge [PROPOSED].** The client is an MCP client of Roblox's built-in Studio server over local stdio, when Studio is open. The server (15, 20) issues a grant per call. The client runs only calls that carry a valid grant, returns the result, and keeps no grants and no audit log. Write tools need owner approval on the server before relay (Question 9). Studio's server has no authentication, so only this client process connects.

**Playtest support [PROPOSED].** Console output, state dumps and scripted input through the bridge. Periodic `screen_capture` only at a rate the owner sets, because screenshots are costly and agents cannot watch a viewport (Soul §2). Godot runs its GUI here only when a human needs it.

**Local NPU [PROPOSED].** Ratified exception (C17). Probe at start: OpenVINO EP on Intel NPU (Core Ultra Series 1+) via Windows ML, then ORT CPU. QNN later. Pin the NPU driver to 32.0.100.5540 or newer; avoid 32.0.100.4300, .4509 and .4512 (crash reports). Cache compiled models keyed on driver plus OpenVINO version. Show the active backend in the UI, and check that ops ran on the NPU rather than falling back silently to CPU. No tokens-per-second figure is sourced; benchmark before any budget.

**Local terminal [PROPOSED].** Ratified exception (C17, C31). One ConPTY per session, for the human's own shells only. It replaces Windows Terminal for local use. Registering as the console host (the default-terminal role) is gated on spike S10 (35). If S10 fails, Bazaar is a terminal the user opens, not the default.

**Start screen [PROPOSED layout].** Sections in order: unread and mentions; awaiting you; agents and Kanban; build health and usage. Fed by one server "since last seen" query (02). Offline, the dashboard shows only the cached profile and derived counters, never message bodies (round 5, C27; doc 27 owns the offline fallback shell).

**OS integration [PROPOSED].** The installer registers per-user ProgIDs and capabilities (HKCU). It never writes UserChoice. An in-app "Make Bazaar default" action opens Windows Settings default apps and explains the one step.

**Installer [PROPOSED].** Velopack per-user install, GitHub Releases feed, delta updates. No service. Velopack does not sign code or install machine-wide (C11), so the release pipeline signs with SignPath (round 4). WebView2 Runtime checked at first run (redistribution terms need a check).

**Vim layer [PROPOSED].** A preview-key handler at the window root. The key-state machine lives in Client.Core so it can be unit tested without a window. Any focused text input is insert mode, so IME composition is never intercepted.

## Data and interfaces
```csharp
public interface IEditorHost : IAsyncDisposable
{
    Task OpenModelAsync(ModelRef model, CancellationToken ct);   // file or diff
    Task ApplyThemeAsync(EditorTheme theme, CancellationToken ct);
    event EventHandler<EditorEvent> Events;                      // edit, focus, save
}
public interface ITerminalHost { Task AttachAsync(TerminalSpec spec, CancellationToken ct); }  // ConPTY behind it
public sealed record StudioRelayRequest(string GrantId, string StudioId, string Tool, JsonElement Args);
public sealed record StudioRelayResult(string GrantId, bool Ok, JsonElement Payload);
```
Contracts: 02 supplies GC, since-last-seen data and the authenticated HTTP surface for blobs and search (round 6, C15); 21 PR, review and merge data; 20 and 15 Studio grants; 14 the owner's signed-approval credential class (C25 follow-up); 27 palette, theme tokens, key router and the offline fallback shell.

## Behaviour and edge cases
- **Renderer crash:** reload the Monaco bundle and reopen models from the server. Unsaved edits stay in a local draft file until the server accepts them.
- **Offline:** dashboard shows cache age; editor goes read-only; reconnect resumes from the sequence number (02).
- **Second launch:** forwarded to the running instance. If it does not answer within 2 s, the launcher starts a new instance and warns.
- **Terminal:** one ConPTY per session. Closing a window with running processes asks first.
- **Merge:** the server refuses a merge without a valid signed approval, and a merged PR moves its card to Done (12).
- **Studio relay:** refused when no Studio is open or the StudioId is unknown. The client allow-list must match the server's.
- **IME:** composition in Monaco and the terminal must work in insert mode (tested on a real desktop).

## Testing
- **Avalonia.Headless:** start-screen layout at three widths; palette ranking; mnemonic routing; detached-window model; theme resources; PR review and merge screens against a fake server. WebView and terminal render as placeholders.
- **Unit tests (no UI):** Vim key-state machine; theme-to-Monaco generator (golden JSON); pipe parser; relay grant check; signed-approval check.
- **Monaco bundle:** a headless Chromium harness with a fake host bridge. Test-only, not a client [PROPOSED].
- **WebView2 and ConPTY:** a Windows CI runner with a fixture shell. Not headless.
- **Handler registration:** a throwaway Windows user account checks ProgID and capability keys. The Settings step is a manual checklist.
- **Studio:** a fake stdio MCP server with recorded responses. No live Studio in CI.
- **GitHub merge path:** a fake GitHub provider; no live credentials in CI.
- **Manual desktop pass:** IME in Monaco and terminal; DPI at 100, 150 and 200 percent; detached-window focus; default-app step.

## Work packages
1. Launcher and single-instance named-pipe IPC (needs 27 shell).
2. Theme-to-Monaco generator and Avalonia resource mapping (needs theme format, Question 4).
3. Monaco bundle build and JS bridge, with harness test.
4. WebView host spike across options B to D, then the host (needs 3; option A excluded).
5. Editor pane with multi-model tabs behind `IEditorHost` (needs 4, 02).
6. Diff and PR/issue panes, in-app review and merge (needs 5, 21, 14).
7. Terminal spike over candidates, then local ConPTY pane (needs 1 for detached windows).
8. Default-terminal handoff spike, S10 (independent).
9. Vim key-state machine and command palette (needs 27).
10. Start-screen dashboard (needs 02 query, 27).
11. Handler registration and "Make default" step (needs 1, 12).
12. Velopack installer, SignPath signing step and WebView2 check (needs 30).
13. NPU probe and Windows ML/OpenVINO wrapper (needs 31).
14. Studio relay client (needs 20, 15).
15. Playtest console and state relay (needs 14, 26).
16. Windows CI lane for ConPTY and WebView2 tests (needs 34).

Packages 1, 3, 7, 8, 9 and 13 can run in parallel.

## Conflicts
1. **Composition embedding** (decided, Windows.md §4) has no verified Avalonia path. The spike may force an HWND-hosted WebView2 with overlay compositing (C01).
2. **Monaco in an Avalonia WebView** is unverified. The spike is a gate for the Avalonia decision (C02; round 5).
3. **Licences of the WebView options** (B, C, and the WebView2 SDK under D) are unverified and must fit the OSI-only build (round 4). **Needs counsel.**
4. **Terminal libraries:** no candidate is verified. The Windows Terminal rendering code (WinUI's `EasyWindowsTerminalControl`) is not reusable under Avalonia (C05).
5. **Roblox:** Studio's MCP server has no authentication and lets clients modify open places. Creator and Open Cloud terms are unchecked. **Needs counsel** (C10).
6. **Velopack** does not sign code (C11). Signing is a separate SignPath step in the release pipeline.
7. **Owner's signed approval (cross-doc, for 14 and 32):** the merge flow needs a device-held signing key for the approval. 32 holds refresh credentials only. 32 must define the key class.
8. **PR merge flow (cross-doc, for 21):** 21 must carry the in-app review and merge flow and both merge-credential options (rounds 8 and 12). Until it does, 21's "owner squash-merges with the owner's credential" line stays as written.

## Questions for the owner
1. **Terminal candidates:** spike Iciclecreek.Avalonia.Terminal and Terminal.Avalonia, with a custom xterm build over ConPTY as fallback? Both candidates must be OSI-licensed. Default: yes.
2. **Process model:** one process, or terminals in a separate host so a GUI crash does not kill shells? Default: one process; revisit after the spike.
3. **Startup budgets:** adopt the table as spike targets? Default: yes.
4. **Theme file:** versioned JSON with named tokens, read by Android and Linux too? Default: yes.
5. **File types to register:** .txt, .md, .json, .yaml, .cs, .py, .ts, .js, .gd, .lua, plus the terminal. Confirm the list. Default: as listed.
6. **Local files:** does the IDE edit files on the human's disk, or only through the server? Default: server-backed; local folders read-only until a save is accepted. (Syncing the viewed branch to a local folder is already ratified, round 4.)
7. **Windows dictation:** in v1 on desktop? Default: no; research Windows speech APIs first (C08: not yet researched).
8. **Desktop NPU:** Intel only for v1? Default: yes; Snapdragon later.
9. **Studio writes:** every write needs owner approval until doc 15 has trust rules. Confirm. Default: yes.
10. **Playtest capture:** scheduled screenshots, owner-reviewable, at a rate set with doc 23's cost model; confirm the rate and the `<Space>` leader key with the mnemonic list. Default: capture off until a rate is set.

ANSWERED: Monaco spike with free routes only; no Accelerate purchase (DECISIONS-LOG round 5, C26; old Q1).
ANSWERED: an HWND-hosted WebView2 is an accepted route if composition is unreachable (DECISIONS-LOG round 4, C23; old Q2).
ANSWERED: signing route is SignPath with an OSI-only signed build; counsel still confirms eligibility and key custody (DECISIONS-LOG round 4, C23; old Q11).
ANSWERED: no paid Avalonia components; no Plus, Pro or Accelerate in v1 (DECISIONS-LOG round 4, C23; round 5, C26; old Q12).
