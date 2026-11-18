# 56. Server TUI: install wizard and live ops

Status: DECIDED for scope (owner decision card, 2026-11-19 02:13 Manila, option "Install wizard + live ops"). PLANNED for mechanics. Module names `Bazaar.Client.Tui` and `Bazaar.Client.Tui.Abstractions` are provisional under the naming rule in DECISIONS-LOG.
Depends on: 03 (host, `bazaar-admin`), 45 (setup wizard, sections 1.1, 1.9, 1.12), 46 (Kodo, section 14), 51 (web admin ports), 32 (roles, sign-in), 15 (approvals, audit), 41 and 55 (mnemonics, Calcinato) · Used by: CLAUDE.md, 03, 45, Bazaar.Server.md

## Purpose
An interactive full-screen terminal app, run over SSH or on the server box. It does two jobs:
1. **Install.** It runs the setup wizard headless, before any client exists.
2. **Live ops.** After install it gives status, logs, Kodo health, modules, users and rebuilds.

Owner's wording: "Install wizard + live ops": an interactive full-screen terminal app (run over SSH or on the box) that runs the setup wizard headless and then gives status, logs, Kodo health, modules, users and rebuilds afterwards. The owner's reason: the server must be installable headless, and it keeps running headless after install.

## Decided inputs
- **A separate client module, never inside the server process.** Bazaar.Server.md section 4 and doc 03: no admin console inside the service. The TUI is `Bazaar.Client.Tui` plus `Bazaar.Client.Tui.Abstractions`, with its own manifest. It adds nothing to `Bazaar.Server` or `Bazaar.Client.Core`.
- **The wizard lives once.** The step list, validation, plan hash, apply and audit are in `Bazaar.Server.Setup`. The TUI is only another `ISetupPrompter` (ask, choose, confirm, secret, show) beside the plain console prompter of `bazaar-admin`. The TUI never re-implements a step.
- **No new privilege and no new server surface.** Live ops reads and writes through the same ports and authorization checks as the web admin panel (doc 51) and the in-client admin views. Every action is audited (doc 15).
- **Library.** Terminal.Gui v2 is the proposed library (Bazaar.Server.Md research note). The package pin, licence and AOT/trim status are checked in work package 1 before it is referenced. Provisional until then.

## Design

### 1. Overlap check (owner rule, 2026-11-19)
| Existing or designed | Relation |
|---|---|
| `bazaar-admin` and its plain-text wizard (docs 03, 45 section 1.1) | Stays. It is the scripted, screen-reader and non-TTY path. The TUI is the full-screen way to the same engine. |
| Setup engine, templates, provisioning API (doc 45 sections 1.5 to 1.9) | Reused, not copied. |
| Kodo verification and monitor (doc 45 section 1.12, doc 46 section 14) | The TUI shows Kodo through its API. Doc 46 section 14.3 already lists a terminal client; this is that client for the server box. |
| Web admin panel (doc 51) | Same ports and permissions, different front end. Where a port is missing (logs), it is added to the port set, once, for both. |
| `Bazaar.Client.Terminal` (dev chat client) and the Calcinato text head (doc 55) | Not extended. They are chat and coding surfaces. The TUI shares the mnemonic and keymap provider (doc 41) and the pane text formatters where they fit, and references no chat code. |

### 2. Modes
| Mode | When | Backend |
|---|---|---|
| Install | No server is running yet, on the box | The setup engine in-process, same as `bazaar-admin setup`: same plan, same hash, same audit. Access rule is doc 45 section 1.9 (loopback and one-time bootstrap code). |
| Ops | Server is running | The admin ports of doc 51, over loopback when run on the box. Remote use is SSH to the box, then the TUI on loopback. A non-loopback bind needs the doc 03 approval. |

The TUI starts in Install mode when no owner exists and in Ops mode afterwards.

### 3. Screens (Ops)
Status and readiness; logs (tail, filter by level and module); Kodo health, incidents and runbook links; modules (selection, build reports, rebuild); users, roles, devices and revocation; approvals; audit view; setup changes through the same wizard (`ISetupPrompter` again). Write actions use the same approval step-up as the clients.

### 4. Input, access and accessibility
- Keyboard only, mnemonics from the doc 41 registry with no Ctrl chords, remappable, plus a palette.
- Falls back to the plain wizard when stdout is not a TTY, with `--plain`, with `NO_COLOR` set, or when a screen-reader flag is given. Doc 45 line "Not a TUI" is replaced: the plain wizard stays as the accessible path; the TUI is opt-in beside it.
- Every state word has text, not only colour. Strings go through Localisation.
- Secrets typed into the TUI are never echoed, logged or kept in the screen buffer (same canary tests as doc 45).

### 5. Authentication in Ops mode (PROPOSED)
Terminals cannot do a passkey ceremony. Default: the TUI authenticates with the same local owner gate as `bazaar-admin` (doc 45 section 1.9), and for other admin accounts with a short-lived code issued from a signed-in client that the TUI redeems for a cookie-style session under the same roles check (doc 51 session store). Owner question 1.

## Open questions for the owner
1. **Ops authentication.** Default as in section 4 above, or only the local owner? Reason: it decides whether non-owner admins can use the TUI.
2. **Remote use beyond SSH.** Default: SSH only. Alternative: allow the TUI to attach over Conduit like the clients.

## Testing
Headless tests of each screen's view model; the wizard conformance suite runs against the plain and the TUI prompter and must produce equal plan hashes; secret canary tests; an architecture test that the module references neither `Bazaar.Server` nor chat client code.

## Work packages
1. Spike: Terminal.Gui v2 pin, licence, trim, test driver, SSH rendering. 
2. `Bazaar.Client.Tui.Abstractions` and the app shell, mode detection, palette, themes.
3. TUI `ISetupPrompter` (after the setup engine move in #816).
4. Ops screens: status, Kodo, logs, modules and rebuild, users, approvals, audit (as the doc 51 ports land).
5. Docs and packaging with the Linux tarball and Windows install.
