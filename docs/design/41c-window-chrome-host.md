# 41c. Window chrome host (direction, live regions, focus order)

Status: PROPOSED. Nothing here is built. It needs an owner card before code. Author: the accessibility and i18n thread, 2026-11-19. Shaped with the Core thread (`MainWindow` stays an adapter in Client.Core) and the GC and Views thread (no shell design existed).
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

## 3. Boundaries

- `Bazaar.Accessibility.Avalonia` gains a reference to `Bazaar.Localisation`. No dependency on Client.Core internals, chat types or the Layout module.
- A head that does not call `Attach` still runs: announcements are dropped and direction stays left to right.
- Settings (client settings, safe shipped default): announcement categories on or off, and the minimum interval per category.

## 4. Tests

Avalonia.Headless tests: direction flips for `ar` and not for `en`, and again on a locale change; an announcement reaches the region and an excess one is held back; a disabled category stays silent; the focus-scope mnemonic moves focus; the gate scan passes on an attached window.

## 5. Open questions

- OPEN: a standalone `Bazaar.Client.Shell` module instead of this helper would need an owner call; no doc plans one.
- OPEN: where the focus-scope names come from when a layout (doc 41) places panes. Proposed: the layout slot name.
- OPEN: mirror-sensitive icon marking. Proposed: an attached property `WindowChrome.MirrorsInRtl` on icons, read by a style set in `Bazaar.Accessibility.Avalonia`.
