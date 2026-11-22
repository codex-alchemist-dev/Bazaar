# 41c. Window chrome host and client shell module (direction, live regions, focus order)

Status: DECIDED (owner, 2026-11-19: "build it all"; both the helper and the shell module). Section 2 is built in the PRs that follow. Author: the accessibility and i18n thread, 2026-11-19. Shaped with the Core thread (`MainWindow` stays an adapter in Client.Core) and the GC and Views thread (no shell design existed).
Depends on: 49-gap-decisions-operations.md section 2 (DECIDED), 41-client-gui-customization-and-dsl.md, 41b-client-module-boundaries.md.

## 1. Problem

Doc 49 section 2 decides three things that act on the whole window, not on one pane:

- Layout supports right-to-left scripts, and mirror-sensitive icons and directional controls are marked.
- Live regions announce new GC messages, mentions, approval requests and Kodo incidents, rate-limited, with a per-category off switch.
- Every interactive control is reachable by keyboard, in a defined focus order.

The pieces exist as pure libraries: `TextDirectionOf` (Bazaar.Localisation), `AnnouncementGate` (Bazaar.Accessibility) and `GatedLiveRegion` (Bazaar.Accessibility.Avalonia). Nothing mounts them. Today `MainWindow` (Bazaar.Client.Core) only replaces its `Content`, and no module owns window chrome. Putting the host in Client.Core would add feature code there, which the architecture ratchet forbids.

## 2. Proposal

No new module. The Core thread pointed out that `Bazaar.Accessibility.Avalonia` and `Bazaar.Localisation` already exist, so a separate shell module would duplicate them (overlap rule), and Client.Core cannot gain code. Instead `Bazaar.Accessibility.Avalonia` gets an attachable helper, `WindowChrome.Attach(Window, ILocaleSource, AnnouncementGate)`, that each head calls once after creating its main window, the same way heads call `ClientModuleHost.Load`. It:

1. **Flow direction.** Reads the active locale from `ILocaleSource`, asks `TextDirectionOf`, and sets `FlowDirection` on the window. A locale change updates it at once, with no restart.
2. **Live region.** Mounts one `GatedLiveRegion` fed by the head's `AnnouncementGate`. Modules announce through a small `IAnnouncer` in `Bazaar.Accessibility` (no Avalonia reference). The held-back count is phrased through the localisation catalogue as an ICU message, never by concatenation.
3. **Focus order.** A named focus-scope list the head fills (for example start dashboard, GC list, composer), with a mnemonic to move between scopes. Reachability stays checked by `AccessibleControlScan` in the gate tests.

`ILocaleSource` is a one-method abstraction in `Bazaar.Localisation`. The edits are the head composition files only; Client.Core is untouched. The Linux and Terminal heads compose no panes yet, so they attach once they do.

### 2a. The shell module (owner: build both)

The owner's standing answer is to build everything the docs name, so the helper is not the only piece. `Bazaar.Client.Shell` and `Bazaar.Client.Shell.Abstractions` also exist:

- `Bazaar.Client.Shell.Abstractions` holds `IShellAnnouncer` (announce a category and text) so feature modules announce without referencing Avalonia or the accessibility module.
- `Bazaar.Client.Shell` implements it over `WindowChrome` and adds `ShellHost.Attach(window, content, ...)`, the single call a head makes: it attaches the chrome, wraps the content and returns the announcer. It carries no logic of its own beyond composition, so the helper remains the one place that sets direction and mounts the region (no duplicate behaviour).
- `ShellHost.Install(window, locale, gate)` is the form heads use once the window's content is already set: it moves that content under the chrome and returns the announcer. Windows and Linux call it today. Android has no window, so `WindowChrome.Attach` and `ShellHost.Install` accept any content host control (a `Window` is one); the Android head installs it on its main view. Until a persisted language setting exists, heads pass `SystemLocaleSource` (in `Bazaar.Localisation`), which starts from the operating system's UI language and can be switched at run time.

### 2b. Text scale

Doc 49 asks that text scales to at least 200 percent without loss of function. `WindowChrome.TextScale` (a `TextScaler`) hosts the wrapped content in a layout transform and scales it between 1.0 and 3.0 (values outside are clamped), so measured size follows the scale and a scrollable pane scrolls instead of clipping. The head sets it from the user's text-size setting; the setting itself is the operator-configurable default (configurability rule).

### 2c. Reduced motion

`Bazaar.Accessibility.MotionPolicy` (also on `WindowChrome.Motion`) is the one place that decides animation length (doc 49: motion can be reduced). Reduced motion makes decorative animation instant, caps meaning-carrying (essential) animation at 120 ms, and turns decorative loops off; a module asks the policy instead of reading the theme flag. The head sets `Reduced` from the theme's `reducedMotion` token or the platform setting.

## 3. Boundaries

- `Bazaar.Accessibility.Avalonia` gains a reference to `Bazaar.Localisation`. No dependency on Client.Core internals, chat types or the Layout module.
- A head that does not call `Attach` still runs: announcements are dropped and direction stays left to right.
- Settings (client settings, safe shipped default): announcement categories on or off, and the minimum interval per category.

## 4. Tests

Avalonia.Headless tests: direction flips for `ar` and not for `en`, and again on a locale change; an announcement reaches the region and an excess one is held back; a disabled category stays silent; the focus-scope mnemonic moves focus; the gate scan passes on an attached window.

## 5. Open questions

- OPEN: where the focus-scope names come from when a layout (doc 41) places panes. Proposed: the layout slot name.
- Mirror-sensitive icon marking (owner: build it all): the attached property `WindowChrome.MirrorsInRtl` on an icon flips it horizontally while its effective flow direction is right-to-left and follows locale changes; unmarked icons never flip.
