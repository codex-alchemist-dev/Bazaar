# 55. Calcinato: the terminal module (server and client halves)

Status: PROPOSED (2026-11-18). Gap-fill under the owner's configurability rule (DECISIONS-LOG round 13) and the "decide the most robust option" rule. Nothing here contradicts a DECIDED item. Items the owner has already decided are cited. Everything else is tagged PROPOSED and can be changed by the owner at any time.
Docs section this implements: this document, plus doc 28 (Local terminal), doc 41 (sections 4.10, 4.11, client-wide mnemonics), doc 16 (Pty messages, guest agent terminals), doc 02 (the `pane` stream), doc 35 (spikes S10 and S11).
Depends on: 16, 02, 27, 28, 30, 31 (NPU suggestions), 41, 41b, 43 (`:crash` commands), 46 (Kodo terminal view), 48 (permission classes).

## 1. What Calcinato is

Calcinato is the terminal of the Bazaar client. It has two jobs that share one UI.

1. **Local terminal (DECIDED, C17 and C31).** The human's own shells on the human's own machine. ConPTY on Windows, a Unix PTY on Linux. This is a client-side compute exception. It never runs agent work.
2. **Agent terminal viewer.** Agent terminals run inside agent VMs (doc 16) and are streamed by the server on the `pane` stream (doc 02). Calcinato renders them with the same emulator. This is the server half.

Names (DECIDED, doc 41 section 4.10): the terminal is **Calcinato**, the CLI styling is **CaLcInato**. Clearance is a later task; keep both out of package IDs that ship publicly until cleared. Project names below are working names under the owner's pattern `Bazaar.Server.<Name>` and `Bazaar.Client.<Name>` (doc 41b section 2a).

## 2. Decisions already in force (do not reopen)

- Vim-style modal model: normal mode for navigation and commands, insert mode for typing into the shell. No Ctrl chords in Calcinato's own UI (doc 41, client-wide mnemonics). Ctrl bytes typed in insert mode are shell input, not Calcinato commands (see 5.3).
- Client-wide mnemonics work inside Calcinato through the palette (doc 41). `Bazaar.Ex` owns the mnemonic grammar and table; Calcinato registers commands there and adds no second router.
- Local NPU suggestions are off by default. Shell completion is the baseline and AI never replaces it. AI suggestions never run a command (doc 41 section 4.11).
- Agent terminals are server-side and streamed; the local terminal is not a guest channel (C31).
- Becoming the OS default console host is gated on spike S10. Until then the user opens Calcinato. Registration only ever guides the user to Settings and never claims a silent default (CLAUDE.md).
- One process, own windows. The terminal shares the client process, the connection and the theme (doc 41 section 4.10).
- The terminal widget must be OSI-licensed for the OSI-only build (C23). S11 passed for both PTY mechanisms with no third-party library. The emulator is written in-tree (no third-party emulation code), so the S11 "fail: own terminal widget" branch is the chosen path by default and the S11 note about the licence holds trivially.

## 3. Module layout (PROPOSED)

```
Server
  Bazaar.Server.Terminal.Abstractions   ITerminalBroker, session and scrollback contracts, IGuestPtyChannel (the seam to Vms)
  Bazaar.Server.Terminal                broker, scrollback ring, access policy, audit hook, pane-stream mapping; registers as an IBazaarComponent (id `terminal`)
Shared wire
  Bazaar.Contracts.Terminal             TerminalFrame types for the `pane` stream; source-generated JSON, registered in the codec
Client
  Bazaar.Client.Calcinato.Abstractions  ITerminalHost, IPtyBackend, IPtySession, TerminalSpec, IScreen, ITerminalSource
  Bazaar.Client.Calcinato               VT parser, screen buffer and scrollback, modal engine, ex command line, session manager, pane view model, module manifest (`calcinato`)
  Bazaar.Client.Calcinato.Pty           platform PTY backends: Windows ConPTY and Unix PTY. The only place Windows-only APIs live (doc 03 provider rule)
```

Rules, all enforced by the architecture tests that already exist:

- Nothing lands in `Bazaar.Server` or `Bazaar.Client.Core` (ratchet).
- No module references `Bazaar.Server.csproj`. Modules reference each other through `.Abstractions` only. The client module references `Bazaar.Client.Core` for the module contract, `Bazaar.Ex` for mnemonics, and `Bazaar.Contracts` for wire types.
- `Bazaar.Client.Calcinato` is platform-neutral. It never calls ConPTY or libc. `Bazaar.Client.Calcinato.Pty` is the single implementer of `IPtyBackend`, chosen by the head.
- The existing `Bazaar.Client.Terminal` project is the **terminal head** (a text-mode client, doc 27) and stays as it is. Calcinato is the terminal *emulator module* the heads compose. Calcinato is not the head.

## 4. Server half: agent terminals (PROPOSED)

The guest agent already speaks `PtyOpen`, `PtyData`, `PtyResize`, `PtyClose` and `PtyExit` (doc 16, section 3; `Bazaar.Contracts.Vms`). The server half adds the client-facing side.

- **Seam.** `IGuestPtyChannel` (in `Bazaar.Server.Terminal.Abstractions`) is what the VM module implements: open a terminal in a VM, write input, resize, close, and an ordered output channel per terminal with exit notice. The terminal module never references `Bazaar.Server.Vms`; the VM module registers its implementation. Until it does, the broker reports "no VM backend" and fails closed. **Built (2026-11-22):** the implementation is `Bazaar.Server.Terminal.Vms` (component `terminal-vms`, an adapter that depends on `terminal`, soft dependency `vms`), which references `Bazaar.Server.Terminal.Abstractions` and `Bazaar.Server.VmTransport.Abstractions` only. It opens a terminal with `IGuestChannel.OpenPtyAsync` on the channel the guest transport's `IGuestChannelDirectory` serves for the agent, so the VM must be a live VM of that agent with an open channel (stopped, saved, retired, unknown and another agent's VMs refuse). Design change in the same PR (docs first, labelled): `IGuestPtyChannel` gains `OpenForAgentAsync(agentId, vmId, terminalId, cols, rows, ct)`, which the broker now always calls, because the plain `OpenAsync` carries no agent and a VM-backed channel cannot check ownership without one; the default implementation calls `OpenAsync`, and the VM adapter refuses the plain overload. Not yet composed by the host (the host does not compose the terminal module).
- **Broker.** `ITerminalBroker` creates and tracks terminal sessions keyed by a terminal id, fans output to subscribed clients, accepts input and resize from permitted clients, and closes sessions. One session has one *writer* at a time (the control holder) and any number of read-only viewers.
- **Scrollback ring.** Each session keeps a bounded ring of recent output bytes so a client that joins late or reconnects gets a replay. The default is 1 MiB per session, an operator setting, with a hard floor of 64 KiB. Output past the ring is dropped from replay only; it is never dropped from live fan-out. Every frame carries a per-session sequence number so a client detects a gap and requests replay from the ring.
- **Access policy.** Fails closed. The owner can read every terminal (the owner reads all, doc 01). Input requires the writer role, granted to the owning agent's session and to a human member who takes control. Taking control of an agent's terminal is an audited action. Untrusted input rules from doc 15 apply: a terminal fed by a tainted session needs approval for high-impact commands, which is the agent runtime's gate and not this module's. This module only enforces who may write bytes.
- **Audit.** Open, close, control taken, control released, and input from a human member write audit rows through the audit abstraction. Output bytes are not audited (they are the scrollback).
- **Backpressure.** Per-client send queues are bounded (default 256 frames). A client that falls behind is dropped to replay on its next frame, never allowed to stall the session or other viewers.
- **Limits (operator settings, safe defaults).** Maximum sessions per agent 4, per company 64; idle close after 60 minutes with no input and no output; maximum frame payload 16 KiB; maximum input rate per client 64 KiB per second.
- **Component.** Registers as `terminal`, hard dependency on none, soft dependencies on `vms` (the channel) and `audit` (rows). A missing soft dependency disables only the features that need it (doc 40 dependency graph).
- **Wire.** Frames travel on the `pane` stream (doc 02). The stream name for a terminal is `pane:<terminalId>`; the bare `pane` stream is the index of terminals the caller may see. See 6.

## 5. Client half: the emulator and the modal UI (PROPOSED)

### 5.1 Emulation

An xterm-compatible subset, written in-tree, pure and testable without a display:

- **Parser.** A state machine after the DEC VT500 model: ground, escape, CSI (parameters, intermediates, final), OSC (terminated by BEL or ST), DCS and SOS/PM/APC consumed and ignored without side effects. Malformed or oversized sequences are discarded, never thrown. Parameter count and string lengths are capped (default 16 parameters, 4096 bytes OSC) so hostile output cannot grow memory.
- **Supported.** Printable text including UTF-8 split across chunk boundaries, wide characters (East Asian width 2 cells) and combining marks, C0 controls (BS, HT, LF, VT, FF, CR, BEL, SO/SI ignored), cursor movement (CUU, CUD, CUF, CUB, CUP, HVP, CHA, VPA, CNL, CPL, save and restore), erase (ED, EL, ECH), insert and delete (ICH, DCH, IL, DL), scroll (SU, SD, DECSTBM region), SGR with 16 colour, 256 colour and truecolour (S11 sequences `31`, `38;5;n`, `38;2;r;g;b`), bold, dim, italic, underline, reverse, strikethrough, hidden, DECSET and DECRST for cursor visibility (25), application cursor keys (1), alternate screen (47, 1047, 1049), bracketed paste (2004), autowrap (7), origin mode (6), mouse reporting modes (1000, 1002, 1006) as capability flags, window title by OSC 0 and 2, hyperlinks OSC 8 (stored, opened only by the user), device attributes replies (DA1) and cursor position report (DSR 6).
- **Never done.** Sixel and other image protocols, OSC 52 clipboard write, window manipulation (CSI t), and any sequence that makes the terminal run a command or read a file. Output from a shell is untrusted. OSC 52 and title reports are refused. Title text is sanitised (control characters stripped, 256-character cap) before it reaches the UI.
- **Screen model.** A grid of cells (code point cluster, width, SGR attributes), a primary screen with scrollback, an alternate screen without. Scrollback default 10,000 lines, a client setting, hard cap 200,000. Resize reflows wrapped lines on the primary screen and clears nothing on the alternate screen.
- **Responses** (DA, DSR) go back through the session's input path and are rate-limited so a hostile stream cannot make the terminal flood the shell.

### 5.2 Modal engine

Pure state machine, no UI types, same style as `Bazaar.Ex`.

- **Modes.** Normal, Insert, Visual (character-wise and line-wise, over scrollback and the screen), Command line (ex).
- **Insert** forwards key input to the session as bytes (UTF-8 text, plus function and arrow keys encoded per the active DECSET modes). `Esc` (or the mnemonic escape `jk` typed quickly, an operator setting off by default) returns to Normal. The remapped Escape key is a setting per person.
- **Normal** navigates the scrollback and screen: `h j k l`, `w b e`, `0 $`, `gg G`, half and full page keys by named keys (PageUp, PageDown, Home, End) and by `u`/`d`/`f`/`b` single keys, `/` and `?` search with `n` `N`, counts (`5j`), marks `m<letter>` and `'<letter>`, `i a I A` to enter Insert, `v V` for Visual, `:` for the command line, `y` to yank the Visual selection to the client clipboard. No Ctrl chords anywhere.
- **Mnemonics.** In Normal mode a typed letter sequence that is not a motion is offered to `Bazaar.Ex` (client-wide mnemonics and the palette), so `ngc1` or `settings` work from the terminal. The motion keys are reserved in the terminal scope and an operator or person remap changes them through the commands document, the same as any command (doc 41).
- **Ctrl-C and other control bytes.** Calcinato's own UI defines no Ctrl chords. In Insert mode a physical Ctrl-C is delivered to the shell as byte 0x03 (S11 criterion; the shell owns it). For people who never use Ctrl, `:interrupt` (and a default Normal-mode mnemonic `intr`) sends 0x03, `:eof` sends 0x04, `:suspend` sends 0x1A. This keeps the doc 41 rule and the S11 Ctrl-C requirement both true.
- **Command line (ex style).** `:` enters the line. Commands: `:q` close terminal, `:q!` close and kill, `:new [shell]`, `:vsplit` and `:split` (pane layout through `Bazaar.Client.Layout`), `:attach <terminalId>` (server terminal), `:detach`, `:take` and `:release` (control of a server terminal), `:set <option>`, `:crash list|preview N|send N|drop N` (doc 43 section 7), `:interrupt`, `:eof`, `:suspend`, `:clear`, `:theme`, `:help`. Unknown commands show an error and change nothing. History is per-session and not persisted by default (it can contain secrets; an operator setting can persist it).
- **AI suggestions** (doc 41 section 4.11): the engine exposes `ISuggestionSource`. The default source returns nothing. The NPU source lives in `Bazaar.Client.LocalInference` and is wired in only when the person has turned the setting on. Suggestions are ghost text, accepted with `Tab` in Insert mode, and never executed without that accept.
- **Accessibility** (doc 49). Every mode change and command result has a speech string through the same live-region feed `Bazaar.Ex` uses. The screen is also exposed as a line-oriented text model for screen readers (the visible rows plus the cursor row), updated on output with a rate limit so a fast stream does not flood the reader.

#### 5.2 addendum: the leader key (PROPOSED, 2026-11-18)

Review found that the modal engine checks only the first key of a typed Normal-mode sequence against the motion keymap. A client-wide mnemonic whose first letter is a motion (`ngc1`, `nrfdrm`, `unread`, `intr`) could never be reached. This addendum replaces nothing above. It adds a leader.

- **Leader.** In Normal mode the leader key (`Space` by default) starts a mnemonic sequence. Every later key is appended to the sequence and offered to the `IMnemonicBridge` with no motion lookup. `intr` is therefore reachable as `<Space>intr`.
- **Keys.** `Esc` cancels. Any navigation key or control byte cancels. `Enter` submits an exact match. `Backspace` removes the last key, and cancels when the sequence is empty.
- **Prefix and exact rules.** While the sequence is a proper prefix of a longer mnemonic the engine waits, with no timeout. An exact match that nothing longer extends runs at once. An exact match that is also a prefix runs on `Enter`, or when the next key cannot extend it. In that last case the key that broke the match is dropped.
- **Built-in intr.** The engine sends Ctrl-C for `intr` inside a leader sequence. `Bazaar.Ex` reserves the letter `i`, so the client-wide table cannot hold `intr`.
- **Remap.** The leader is a `ModalKeymap` setting. It may be Space or a printable ASCII character that is not a digit and is not bound to a motion. Binding a motion to the leader fails. `Clone` keeps it.
- **Scope.** The leader applies only in Normal mode. Visual and Insert are unchanged. The existing first-key path stays for mnemonics that do not begin with a motion key.
- **Status.** PROPOSED. Not yet recorded in `docs/design/DECISIONS-LOG.md`. The owner decides before it becomes DECIDED.

### 5.3 Sessions and sources

- `ITerminalSource` is one byte stream plus size and exit: either a **local** source (an `IPtySession` from the head's `IPtyBackend`) or a **remote** source (a server terminal over the `pane` stream through the wire client). The engine does not know which.
- The session manager owns sessions, the active session, split layout state and the title. Closing a window with a running local process asks first (doc 28).
- Remote sources replay from the scrollback ring on attach and on reconnect, detect sequence gaps, and show a "replay" marker in the status line. A viewer without the writer role is read-only: Insert mode is refused with a message, Normal mode still works.
- **Backend contract.** `IPtyBackend.Open(TerminalSpec)` returns an `IPtySession` with `Output`, `Exited`, `WriteAsync`, `Resize`. The Windows backend must call `SetConsoleCtrlHandler(NULL, FALSE)` before creating the pseudo console (S11 finding) and ends the process tree on close. The Unix backend uses `posix_openpt`, `ioctl(TIOCSWINSZ)` and `posix_spawn` with a new session, as the S11 Linux harness does. Shell choice order: spec shell, then `SHELL`, then `pwsh`, `cmd` on Windows or `bash`, `sh` on Linux. Environment passes through minus names on a deny list (Bazaar tokens). Working directory defaults to the user's home.

### 5.4 View

The pane view model (`TerminalPaneViewModel`) exposes the visible cells, cursor, mode, status line and the accessible text model. Rendering is a head concern: an Avalonia control in `Bazaar.Client.Core`'s adapter slot that draws cells from the view model using the theme's terminal colour tokens (the theme file drives Avalonia and Monaco, doc 41). The text-mode terminal head renders the same screen with ANSI. `Avalonia.Headless` tests cover the view model and the control layout with a fake backend (CLAUDE.md).

## 6. Wire (PROPOSED)

`Bazaar.Contracts.Terminal` defines JSON frames, source-generated, camelCase, unknown fields skipped (same pattern as `GuestJsonContext`). Payload bytes are base64 in JSON (`System.Text.Json` default for `byte[]`).

| Frame | Direction | Fields |
|---|---|---|
| `TerminalOpen` | client to server | `terminalId`, `agentId` (or null for a human-owned VM shell), `cols`, `rows` |
| `TerminalOpened` | server to client | `terminalId`, `writerHeld`, `replayFrom` |
| `TerminalOutput` | server to client | `terminalId`, `seq`, `bytes` |
| `TerminalInput` | client to server | `terminalId`, `bytes` |
| `TerminalResize` | client to server | `terminalId`, `cols`, `rows` |
| `TerminalControl` | both | `terminalId`, `action` (`take`, `release`, `granted`, `denied`) |
| `TerminalReplay` | client to server | `terminalId`, `fromSeq` |
| `TerminalExit` | server to client | `terminalId`, `code` |
| `TerminalClose` | client to server | `terminalId` |
| `TerminalError` | server to client | `terminalId`, `code`, `message` |

Sequence numbers start at 1 per terminal and never repeat. The `pane` stream is state (doc 02): a client that resubscribes sends `TerminalReplay` and gets the ring, not an event log. Terminal frames are never part of the GC log and never persisted beyond the ring.

### 6.1 Carriage in the doc 02 envelope (PROPOSED)

Terminal frames ride the connection's normal envelope, so every transport and codec in doc 90 carries them unchanged. One new message type, `pane.frame` (`WireTypes.PaneFrame`), used on stream `pane:<terminalId>`:

- Server to client: an `event` envelope, `s` = `pane:<terminalId>`, payload `{ "frame": <string> }`, the terminal frame as JSON text. A future binary form would be a new optional field (for example `frameBin`), never a change to `frame`, so old peers keep working.
- Client to server: a `request` envelope with the same type and payload. The server answers `ok` once the frame is accepted or an `error` envelope with a `TerminalError` code. Request ids make a resent frame idempotent after a reconnect (doc 02).
- The frame stays JSON text inside the payload, so the codec of the connection (JSON, CBOR, MessagePack and others) only has to carry one string field and a terminal never depends on the connection codec.
- When a transport maps each subscription to its own stream (doc 90 `stream_per_subscription`, not built yet) the envelope stays identical and only the carrier changes. Subscribe, unsubscribe, `lagged` and `stream.revoked` work as for any stream. The pane stream is state: the server ignores the cursor and a resubscribe is answered by the client sending `TerminalReplay`.

The client side adapter lives in the wire client module that takes the `Bazaar.Wire.Abstractions` types, not in Client.Core: a `StreamSink` extension point there takes one handler per stream prefix, with a default of "unknown stream", so Client.Core gains no terminal code. Calcinato's `IPaneStreamConnection` is implemented on that sink.

## 7. Registration and distribution

- **Manifest.** `Bazaar.Client.Calcinato` declares module id `calcinato`, the commands it contributes (`terminal`, `newterm`, `attach`, `intr`), the `Bazaar.Ex` scope, the permission classes it needs from doc 48 section 1 (a local process class for the PTY, a network class for remote attach), and its optional dependency on `Bazaar.Client.LocalInference`. Server module id `terminal`.
- **Handlers.** Windows registers the terminal handler through the head (doc 28), and the S10 default-console handoff stays gated. Linux uses `.desktop` and `xdg-mime` (doc 30). Android does not host a local shell (no local terminal on Android in v1, doc 29); it can attach to a server terminal read-only or as writer in the phone pane (`PhoneWidgetPolicy` already lists terminal as a widget that needs the wide layout).
- **Telemetry** (doc 43): terminal output and input are never captured. Only opened, closed, error codes and parser rejection counters leave the device, and only under the person's consent setting.

## 8. Tests

- Pure unit tests for the parser (golden byte streams from the S11 colour cases, split UTF-8, malformed sequences, size caps), the screen buffer (wrap, scroll region, alternate screen, resize reflow, wide characters), the modal engine (mode transitions, counts, motions, search, ex commands, mnemonic hand-off), the session manager and replay gap detection.
- Fuzz-style table tests: random byte streams never throw and never exceed the memory caps.
- Server: broker fan-out, ring wrap, replay, gap, writer exclusivity, access-policy matrix, limits, backpressure drop.
- Unix PTY backend: the S11 Linux checks (marker echo, resize, colour, Ctrl-C, clean shutdown) run on Linux CI. ConPTY backend tests run on the Windows lane only (doc 34). They are skipped on other systems with an explicit reason.
- Architecture tests: no new files in Server or Client.Core, no module reference to `Bazaar.Server.csproj`, no Windows-only API outside `Bazaar.Client.Calcinato.Pty`.

## 9. Work packages

1. Docs: this document, doc 40 entry, FEATURES.md rows, README index. (this PR)
2. Contracts: `Bazaar.Contracts.Terminal` frames and codec registration.
3. Client abstractions: `Bazaar.Client.Calcinato.Abstractions`.
4. Emulator: parser, screen buffer, scrollback.
5. Modal engine and ex command line, mnemonics hand-off.
6. Session manager, sources, pane view model, module manifest.
7. PTY backends: Unix first (testable in the cloud), ConPTY (Windows lane).
8. Server abstractions and broker, scrollback ring, access policy, component registration.
9. Head wiring: Windows and Linux Avalonia control, terminal head text rendering, handler registration. Gated on the heads' own work and S10 for the default-console part.
10. Spikes still open: S10 (default console handoff, Windows desktop) and the Avalonia control rendering throughput.

## 10. Open questions (owner)

1. **Persisting command history** across sessions, off by default here. Is that the right default?
2. **Agent terminal write by humans.** This doc lets any human member take control with an audit row. Should it need an owner approval class instead?
3. **Ring size default** (1 MiB) and idle close (60 minutes) are guesses pending measurement.
4. **Clearance** of the names Calcinato and CaLcInato before any public package ID.
