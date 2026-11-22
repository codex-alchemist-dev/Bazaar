# 58. Server TUI: install wizard and live ops

Status: DECIDED for scope (owner decision card, 2026-11-19 02:13 Manila, option "Install wizard + live ops"). PLANNED for mechanics. Module names `Bazaar.Client.Tui` and `Bazaar.Client.Tui.Abstractions` are provisional under the naming rule in DECISIONS-LOG.
Depends on: 03 (host, `bazaar-admin`), 45 (setup wizard, sections 1.1, 1.9, 1.12), 46 (Kodo, section 14), 51 (web admin ports), 32 (roles, sign-in), 15 (approvals, audit), 41 and 55 (mnemonics, Calcinato) · Used by: CLAUDE.md, 03, 45, Bazaar.Server.md

## Purpose
Two roles, one module family, both text-only because the server is headless (owner, 2026-11-19):
1. **Installer TUI.** The server installer comes in a full-screen terminal version and a graphical (GUI) version. Both run the same setup wizard. The TUI version is for a box with no desktop and for SSH.
2. **Resident TUI.** A separate TUI that runs 24/7 on the headless server after install: a persistent console dashboard for status, logs, Kodo health, modules, users and rebuilds. It is not only an on-demand tool.

Owner's wording: "Install wizard + live ops": an interactive full-screen terminal app (run over SSH or on the box) that runs the setup wizard headless and then gives status, logs, Kodo health, modules, users and rebuilds afterwards. Clarified 2026-11-19: "we'll be using TUI in installer (beside having a GUI version installer too) and separately the TUI that runs 24/7 after installation since the server itself is headless."

**Gap to close first.** The docs describe no graphical server installer: doc 30 line 71 plans an MSI (WiX) for the Windows server, and doc 45 section 1.1 lists the plain wizard and the in-client wizard only. The GUI installer is the owner's stated intent, but its design (technology, platforms, who builds it) belongs to the onboarding work, not to this doc. This doc requires only that the GUI installer and the installer TUI share one wizard engine (`ISetupPrompter`), so the wizard logic exists once.

**Two programs (DECIDED, owner card 2026-11-18, "Two separate programs").** The installer TUI goes away after setup. The resident TUI stays. They share one kit so nothing is written twice: `Bazaar.Client.Tui` (shell, theme, palette, widgets) with `Bazaar.Client.Tui.Abstractions`, then two heads on it, `Bazaar.Client.Tui.Installer` (the `ISetupPrompter` and report renderer) and `Bazaar.Client.Tui.Dashboard` (the Ops screens). Names are provisional.

## Decided inputs
- **A separate client module, never inside the server process.** Bazaar.Server.md section 4 and doc 03: no admin console inside the service. The TUI is `Bazaar.Client.Tui` plus `Bazaar.Client.Tui.Abstractions`, with its own manifest. It adds nothing to `Bazaar.Server` or `Bazaar.Client.Core`.
- **The wizard lives once.** The step list, validation, plan hash, apply and audit are in `Bazaar.Server.Setup`. The TUI is only another `ISetupPrompter` (`ShowMessage`, `ShowProgress`, `AskText`, `ChooseOne`, `ChooseMany`, `Confirm`, `AskSecret`; in `Bazaar.Server.Setup.Abstractions`, PR #824), driven by `SetupWizardEngine.RunAsync` with `SetupWizardSteps.Create()` and applied through `SetupWizardBackend` (`ISetupWizardBackend`, over `SetupOrchestrator`: validate, dry run, apply; `ICliApplyGate`, `SetupChangeView`, neutral `SetupReport` types). The backend takes an `ISetupReportRenderer`, so the TUI supplies its own renderer as well as its prompter beside the plain console prompter of `bazaar-admin`. The TUI never re-implements a step.
- **No new privilege and no new server surface.** Live ops reads and writes through the same ports and authorization checks as the web admin panel (doc 51) and the in-client admin views. Every action is audited (doc 15).
- **Library.** Terminal.Gui v2 (2.6.0, MIT). The pin, licence and trim and AOT status were checked in work package 1; only `Bazaar.Client.Tui.TerminalGui` references it, behind `IPromptWidgets` and the shell. SSH rendering is still unchecked.

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
| Install (installer TUI) | No server is running yet, on the box | The setup engine in-process, same as `bazaar-admin setup`: same plan, same hash, same audit. Access rule is doc 45 section 1.9 (loopback and one-time bootstrap code). |
| Ops (resident TUI, runs 24/7) | Server is running | The admin ports of doc 51. Used mainly local, headless and over SSH (owner, 2026-11-19: "TUI is mainly for local, headless, ssh"). Remote use of the dashboard is mainly the HTML admin panel of doc 51. The TUI over Conduit is secondary and not a v1 goal. A non-loopback bind needs the doc 03 approval. |

The TUI starts in Install mode when no owner exists and in Ops mode afterwards.

### 2a. Installer flow (DECIDED, owner card 2026-11-18, "Full walkthrough every time")
The installer TUI asks every setup step in the order of doc 45 section 1.3, one question at a time, with no short path. **The fastest install needs no wizard at all (owner, 2026-11-22): a headless install from a config file (`bazaar-admin setup --config <path>`, doc 45 section 1.5).** The TUI is for people who want to be walked through it. Scripted installs keep that door; the TUI does not offer a quick path or ask for a config file first. Each step shows its safe default, and the dry-run summary (doc 45) is shown before anything is applied.

### 3. Screens (Ops)
**Scope of the resident TUI (owner, 2026-11-19): server health, not the work.** It shows no agent or GC activity. Admin actions (modules, rebuilds, users) stay available as in section 4, but the screen is about whether the server is healthy.

**Default screen (DECIDED, owner card 2026-11-18): health overview.** The resident TUI opens on one screen with server and Kodo health, anything failing, and recent health events. Every other screen is one mnemonic away. It must fit a small terminal (80x24).

Status and readiness; logs (tail, filter by level and module); Kodo health, incidents and runbook links; modules (selection, build reports, rebuild); users, roles, devices and revocation; approvals; audit view; setup changes through the same wizard (`ISetupPrompter` again). **Permissions (DECIDED, owner card 2026-11-18): everything the apps allow.** An admin can do in the TUI whatever the same account can do in a client, including modules, rebuilds, users, roles and settings. Risky actions keep the usual approval step-up. The TUI adds no privilege.

**Menus (DECIDED, owner card 2026-11-18, "Health first, admin tucked away").** The top-level menus are Health, Kodo, Logs and Events. Modules, Users and Approvals sit under one Admin menu, with audit and rebuilds reachable from there. Admin actions follow the permissions above.

### 3a. Alerts (DECIDED, owner card 2026-11-18, "Banner for serious, quiet for minor")
The resident TUI follows Kodo's severity grades (doc 46). A serious failure shows a banner at the top with what failed, and rings the terminal bell once; the banner stays until acknowledged. A minor one only turns its line amber on the overview, with no banner and no sound. Colour is never the only signal (section 4). The bell can be turned off in settings.

**Crash of the TUI itself (DECIDED, owner card 2026-11-18, "Restart itself automatically").** The resident TUI is a separate program from the server, so its crash never affects the server. The service manager (Windows Service or systemd, doc 03 provider) relaunches it within seconds, with bounded restart delays like the server's (`host.service.restart_delays_minutes`). A crash is logged and shown as a line on the next overview.

**Health wins (owner, 2026-11-19: "when it fixed something by itself, or survived a fatal crash").** Good news is health good news: Kodo self-heal and recovery events, a restart that worked, a fatal crash survived, an A/B slot rollback that restored service, a backup or restore that verified. They appear as events on the overview. **DECIDED (owner card 2026-11-18, "Banner plus a daily summary line"):** bigger wins, such as surviving a fatal crash, show a green banner at the top until dismissed; smaller fixes are green lines in the event list; and the overview shows a daily summary line such as "N things fixed themselves today".

### 4. Input, access and accessibility
- Keyboard only. **Navigation (DECIDED, owner card 2026-11-18, "Arrow keys and a menu bar"):** a visible menu bar moved with arrow keys and Enter. This is the owner's choice for the TUI and departs from the client-wide mnemonic-and-palette default of doc 41, which does not bind a terminal head that the owner designs otherwise. **Extras (DECIDED, owner card 2026-11-18, "Yes, both as optional extras"):** letter shortcuts (underlined in the menus) and a type-to-search box are also offered. They are optional; the menu bar stays the main way. Shortcuts are single letters with no Ctrl chords and are remappable.
- Falls back to the plain wizard when stdout is not a TTY, with `--plain`, with `NO_COLOR` set, or when a screen-reader flag is given. Doc 45 line "Not a TUI" is replaced: the plain wizard stays as the accessible path; the TUI is opt-in beside it.
- **Look (DECIDED, owner card 2026-11-22, "Fixed built-in look"):** one built-in dark style that works on any terminal. It is not themeable and does not read the Bazaar theme file; this is the owner's choice for the TUI and departs from the themeable-UI default for the apps.
- Every state word has text, not only colour. Strings go through Localisation.
- Secrets typed into the TUI are never echoed, logged or kept in the screen buffer (same canary tests as doc 45).

### 5. Authentication in Ops mode (DECIDED, owner card 2026-11-18, "Owner on the box, plus admin codes")
Terminals cannot do a passkey ceremony. The owner signs in locally on the box through the same local owner gate as `bazaar-admin` (doc 45 section 1.9). Other admins get a short-lived code issued from a signed-in client and redeem it in the TUI for a session under the same roles check (doc 51 session store). Codes are single-use and audited.

**Remote sign-in (DECIDED, owner card 2026-11-18, "Both, owner chooses per machine"; applies if the TUI is ever used over Conduit, which is secondary, see section 2).** The owner's local gate does not travel, so remotely the owner (and any admin) signs in with the same single-use code from a signed-in client. A machine may also be given a device key, registered once from a signed-in client or on the box, after which sign-in from that machine is signed by the key with no code. The owner chooses per machine, and every grant and revocation is audited.

## Open questions
1. **GUI installer.** DECIDED (owner card 2026-11-22, "Setup thread designs it in doc 45"): the onboarding thread designs the GUI installer in doc 45. This doc only requires that it share the same wizard engine.

## Testing
Headless tests of each screen's view model; the wizard conformance suite runs against the plain and the TUI prompter and must produce equal plan hashes; secret canary tests; an architecture test that the module references neither `Bazaar.Server` nor chat client code.

## Work packages
1. Spike: Terminal.Gui v2 pin, licence, trim, test driver, SSH rendering. **Done except SSH (2026-11-22).** Terminal.Gui 2.6.0 (MIT) is pinned in `Directory.Packages.props` and restores and builds on the repo's .NET 11 SDK (11.0.100) with warnings as errors. Findings:
   - **Package.** It ships a `net10.0` build only, which a `net11.0` project consumes without change. It brings Markdig, TextMateSharp (with a native `libonigwrap` library), ColorHelper, `System.IO.Abstractions` and `Microsoft.Extensions.*` 11.0 as transitive packages; none is referenced by this repo's other modules, and only `Bazaar.Client.Tui.TerminalGui` references the package.
   - **Trim.** A self-contained `linux-x64` publish with `PublishTrimmed` and `TrimMode=full` of an app that starts the host and builds the shell gives no trim warnings and runs; `Terminal.Gui.dll` shrinks from 2.0 MB to 0.8 MB (the whole publish is 24 MB). The assembly is not marked trimmable, so the default partial mode leaves it whole; use `TrimMode=full` when the Dashboard head publishes.
   - **AOT.** `PublishAot` (ILCompiler 11.0.0, `linux-x64`) of the same app gives no warnings and a 9 MB native program (plus `libonigwrap.so`) that starts the ANSI driver and builds the shell. This is a smoke test only: the native build did not run a dialog or the key loop. The new module sets `IsAotCompatible`, and its own code builds clean under that.
   - **Test driver.** Version 2.6.0 has no fake driver; its drivers are `windows`, `dotnet` and `ansi`. `Application.Create(new VirtualTimeProvider())` then `Init("ansi")` runs with no terminal (80x25 by default, `Driver.SetScreenSize` changes it), `Driver.ToString()` returns the screen as text, and `app.InjectKey` feeds keys from the `Iteration` event. The screen lags the keys by one or two loop iterations, so tests let the loop settle before they read it. `Bazaar.Client.Tui.TerminalGui.Tests` is built on this (`HeadlessSession`).
   - **Gotchas** now handled in the module: a `Dialog` with a fixed height hides the row under its first label (use `Dim.Auto()`); a dialog starts focused on, and Enter takes, its last button, so the question's own view is focused when the dialog is modal and Enter on a field or list accepts explicitly; a `ListView` has nothing selected at first, so Space did nothing until a key moved; Esc is Terminal.Gui's own cancel; a secret field masks with the character U+2219; Terminal.Gui can read its own configuration files, so every view gets an explicit scheme and the fixed look never depends on them.
   - **Not checked: SSH rendering.** The build container has no real terminal. The `ansi` driver writes plain escape sequences and is the one to try first over SSH, on a real terminal, before the Dashboard head ships.
2. `Bazaar.Client.Tui.Abstractions` and the app shell, mode detection, palette, themes. **Done for the shell (2026-11-22).** Ports, menu model and state, mode detection and the console widget set as before. New in `Bazaar.Client.Tui.TerminalGui`: `TerminalGuiHost` (owns the one application; created only for `TuiMode.FullScreen`, so the plain path is untouched), `TerminalGuiPromptWidgets` (`IPromptWidgets` as modal dialogs: text, masked secret, pick one, pick many with Space, yes or no; Esc and Ctrl+C give null; a secret is cleared from the field as the dialog closes and never reaches the screen), and `TuiShell` (menu bar drawn from `MenuBarState` with arrow keys, Enter and underlined letter shortcuts; one banner slot under the bar for 3a with the kind written as a word, `SERIOUS` or `OK`, the terminal bell once for a serious one and an Esc acknowledgement; a content area; a status line on the last row; type-to-search; the one fixed dark look in `FixedLook`, which does not read the Bazaar theme file). Type-to-search opens with `/`, because the optional letter shortcuts already use every letter (doc 41's palette mnemonic does not apply here); this is a shipped default, not a decision. It fits 80x24. Not done: the Dashboard screens, the minor-failure amber line and the daily summary line (content of the overview, not the shell), and the settings switch for the bell (the shell takes `ShellOptions.BellEnabled`).
3. TUI `ISetupPrompter` (after the setup engine move in #816). **Done in `Bazaar.Client.Tui.Installer`:** `TuiSetupPrompter` and `TuiSetupReportRenderer` driven by `SetupWizardEngine` with `SetupWizardSteps.Create()` over `SetupWizardBackend`; the plain wizard runs when not a TTY, with `--plain`, `--screen-reader` or `NO_COLOR` (the host passes its own plain prompter). A secret is read without echo and never written to the screen. Headless tests include the secret canary and a check that the TUI path gives the flag door's plan hash. The installer is not yet wired into `bazaar-admin` or a host executable, and it is still a line-and-list console flow. **Next slice:** the Terminal.Gui widgets exist (work package 2), but `TuiSetupPrompter` and `TuiSetupReportRenderer` still write messages, progress and the dry-run report through `ITerminal`. While Terminal.Gui owns the screen those lines would corrupt it, so the full-screen path needs an `ITerminal`-free sink (a message dialog and a progress dialog in `Bazaar.Client.Tui.TerminalGui`) before `TuiInstaller` picks `TerminalGuiPromptWidgets` for `TuiMode.FullScreen`.
4. Ops screens: status, Kodo, logs, modules and rebuild, users, approvals, audit (as the doc 51 ports land).
5. Docs and packaging with the Linux tarball and Windows install.
