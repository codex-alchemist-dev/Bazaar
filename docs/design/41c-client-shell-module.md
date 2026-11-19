# 41c. Client shell module (window chrome host)

Status: PROPOSED. Nothing here is built. It needs an owner card before code. Author: the accessibility and i18n thread, 2026-11-19. Confirmed as a gap by the GC and Views thread (no shell design exists; `MainWindow` lives in Client.Core and is owned by the Core thread).
Depends on: 49-gap-decisions-operations.md section 2 (DECIDED), 41-client-gui-customization-and-dsl.md, 41b-client-module-boundaries.md.

## 1. Problem

Doc 49 section 2 decides three things that act on the whole window, not on one pane:

- Layout supports right-to-left scripts, and mirror-sensitive icons and directional controls are marked.
- Live regions announce new GC messages, mentions, approval requests and Kodo incidents, rate-limited, with a per-category off switch.
- Every interactive control is reachable by keyboard, in a defined focus order.

The pieces exist as pure libraries: `TextDirectionOf` (Bazaar.Localisation), `AnnouncementGate` (Bazaar.Accessibility) and `GatedLiveRegion` (Bazaar.Accessibility.Avalonia). Nothing mounts them. Today `MainWindow` (Bazaar.Client.Core) only replaces its `Content`, and no module owns window chrome. Putting the host in Client.Core would add feature code there, which the architecture ratchet forbids.

## 2. Proposal

A new module pair, `Bazaar.Client.Shell` and `Bazaar.Client.Shell.Abstractions`, that wraps a head's window content. It takes the content `Control` (the Core thread's `MainWindow(Control?)` change in #884 already allows any control as content) and returns the host control. The head passes the result as the window's content.

The shell owns:

1. **Flow direction.** Reads the active locale, asks `TextDirectionOf`, and sets `FlowDirection` on the host. Changing the locale updates it at once, with no restart.
2. **Live regions.** Mounts one `GatedLiveRegion` per window. Modules announce through `IShellAnnouncer.Announce(AnnouncementCategory, string)`, an abstraction in `.Abstractions`, so a module needs no reference to Avalonia. The held-back count is phrased through the localisation catalogue (an ICU message), never by concatenation. Per-category settings come from the client settings store.
3. **Focus order.** A named focus-scope list the head fills (for example start dashboard, GC list, composer). A mnemonic moves between scopes. Reachability is checked by the existing `AccessibleControlScan` in the gate tests.

## 3. Boundaries

- Depends on `Bazaar.Localisation`, `Bazaar.Accessibility(.Avalonia)` and `Bazaar.Client.Shell.Abstractions`. No dependency on Client.Core internals, chat types or the Layout module (layout places panes inside the shell, not the other way round).
- Feature modules depend only on `Bazaar.Client.Shell.Abstractions`. Hard edges stay acyclic. A head without the shell still runs: announcements are dropped and direction stays left to right, and only those features degrade.
- Settings (all operator or client settings with a safe shipped default): announcement categories on or off, and the minimum interval per category.

## 4. Tests

Avalonia.Headless tests: direction flips for `ar` and not for `en`; an announcement reaches the region and an excess one is held back; a disabled category stays silent; the focus-scope mnemonic moves focus; the gate scan passes on a shell-wrapped window.

## 5. Open questions

- OPEN: whether `Bazaar.Client.Shell` also owns window title and theme application, or those stay with the theme module.
- OPEN: where the focus-scope names come from when a layout (doc 41) places panes. Proposed: the layout slot name.
- OPEN: mirror-sensitive icon marking. Proposed: an attached property `Shell.MirrorsInRtl` on icons, read by the shell's style set.
