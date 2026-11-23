# Client visual system, materials and motion (desktop and mobile)

Status: direction approved by the owner 2026-11-22 ("Seems good"); details PROPOSED · Open questions: answered with recommendations, see the end
Depends on: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md, 41-client-gui-customization-and-dsl.md, constellation-render-core.md · Used by: 34-testing-strategy.md

## Purpose
Docs 27 to 29 and 41 fix the client's structure (panes, mnemonic keyboard model, radial menu, theme file) but not how it looks, feels or moves. This doc sets the visual system (themes, colour, type, spacing, materials) and the motion language, and states where desktop and phone deliberately differ. The options and live samples the owner chose from are in the project thread of 2026-11-22.

## Decided inputs
Owner picks, 2026-11-22 (project thread "Design desktop and mobile GUI in detail"). These are recorded as the owner's words as relayed; the owner should confirm them in the PR.
- **Phone navigation is swiping; every other function is in the radial menu.** This is what docs 29 and 41 section 4.8 already say (GC pager, swipeable panes, "top navigation" on the cover screen, radial with dictation at the centre). There is no bottom bar.
- **All three themes ship and are switchable:** Void, Graphite and Paper.
- **Very colourful**, in the manner of the Constellation node colours, shared by all three themes.
- **Acrylic and translucent materials** where the platform supports them.
- **Motion is Fluid, and there is a lot of it**, including Android predictive back and swipe-driven transitions.
- Already decided elsewhere and unchanged: Avalonia only, OSI-only build (no paid components), one theme file drives Avalonia and Monaco, no Ctrl chords, reduced-motion token, WCAG 2.2 AA text contrast, Android is a development console (docs 27, 29).

## Design

### Desktop and phone split [PROPOSED]
| Concern | Desktop (Windows, Linux) | Phone and foldable (Android) |
|---|---|---|
| Navigation | GC rail, chat, pane tabs (doc 41 4.9); mnemonics and palette | Swipe between chats (folded) or panes (unfolded); chat-name strip at the top; radial for everything else |
| Primary input | Keyboard, pointer | Touch, hold, swipe, voice |
| Targets | 28 px rows compact, 36 px comfortable | 48 dp minimum |
| Density | Several panes at once; compact option | One thing per screen |
| Secondary actions | Hover toolbar, context menu | Row swipe (approve, reply, archive), long-press sheet |
| Feedback | Focus ring, hover, cursor | Haptics, pressed states, no hover |
| Motion scale | About half the phone durations, no overshoot | Full Fluid, springs |
| Material | Mica or acrylic on window and rail; opaque editor and message list | Translucent sheets, top strip and radial; opaque message list |

### Phone gestures and discoverability [PROPOSED]
- Swipe left or right: previous or next chat or pane. The neighbour follows the finger and a few pixels of it peek at the edge, so the gesture is visible.
- Swipe a row: approve, reply or archive, with a haptic tick at the commit point.
- Hold empty space for 400 ms: radial, dictation at the centre (doc 29). Hold the composer: dictation. Composer placeholder reads "Hold to dictate".
- Sheets drag with the finger and snap at half and full height.
- Top strip: the chat names, current one highlighted, tappable.
- First run shows two short prompts (swipe, then hold) once; they can be replayed from settings.
- **Accessibility:** gestures are never the only route. The pager exposes custom accessibility actions (Next chat, Previous chat, Open menu). Under TalkBack the radial is presented as a standard action list with the same sectors. Swipe recognition ignores the outer 24 dp so the system back gesture wins. This adds to, and does not replace, the doc 29 spike list.

### Predictive back [PROPOSED; platform support unverified]
Android 14+ predictive back is opted in through `android:enableOnBackInvokedCallback`. The back progress (0 to 1, with the swipe edge) drives the same transition as an in-app swipe: the current screen scales and shifts, the one beneath shows through, and releasing commits or cancels with the Fluid spring. Whether Avalonia 12's Android head exposes the back progress callback is **not known**; a spike confirms it, and if it does not, the head implements `OnBackInvokedCallback` itself and forwards progress through an `IBackGestureSource` interface in Core (same pattern as `IPostureSource`). Below Android 14 back is an instant pop with the standard fade.

### Themes [PROPOSED]
Three base themes, all from one theme file each (doc 27): **Void** (pure black ground, matches the Constellation void and saves OLED power), **Graphite** (blue-grey layers, the soft long-session theme), **Paper** (warm light grey). Void is the shipped default. The theme choice is a per-person setting and may be set by a company layout as a default (doc 41 scopes). Imported theme files keep working.

### Colour [PROPOSED]
- Each theme defines neutrals (background, surfaces, borders, text) and one shared **hue family** used for accents, role and model badges, presence, cluster and node colours in panes, Kanban columns, charts and the radial sectors.
- The hue family follows the Constellation's own approach: hues stepped by the golden ratio (about 137.5 degrees) so any number of items get distinct colours, at a fixed lightness and chroma per theme (OKLCH), chosen so that each hue passes 4.5:1 as text on its theme's surfaces and 3:1 as a graphic. Void and Graphite use brighter, higher-chroma stops; Paper uses deeper stops of the same hues.
- The exact stops are generated, not hand-picked: a pure function `HueFamily(theme, index)` with golden tests, so a pane and the Constellation render the same hue for the same item.
- Meaning never rests on colour alone: badges carry text, health states carry an icon or word (doc 27).

### Materials: acrylic and translucency [PROPOSED]
- **Windows:** window backdrop through Avalonia's transparency hints, preferring Mica where available (Windows 11), then acrylic blur, then opaque. The rail, top strip, palette, sheets and detached tool windows use translucent surfaces over the backdrop.
- **Linux:** blur is compositor-dependent and not guaranteed. The material falls back to a tinted translucent surface, then to opaque.
- **Android:** a tinted translucent surface with a subtle border and elevation shadow; real blur only if the head can do it within the frame budget on the device, otherwise no blur. No claim of blur on Android until measured.
- **Never translucent:** message lists, editor, terminal, diffs and any surface carrying long-form text. Translucent surfaces sit on chrome only.
- **Contrast:** the theme validator checks text on translucent surfaces against the worst-case backdrop (brightest and darkest wallpaper), using the surface's opaque fallback colour as the floor.
- **Fallbacks are automatic:** the system "reduce transparency" setting, battery saver, a failed backdrop request or a frame-budget miss switches to the opaque fallback. A setting `client.materials` (auto, translucent, opaque; default auto) lets the person force it.
- Blur costs GPU time; materials share the client's adaptive frame budget and the renderer's rule of drawing nothing at rest.

### Type, spacing and shape [PROPOSED]
- Fonts, bundled and open: Space Grotesk (headings), Inter (body and UI), JetBrains Mono (code, cost chips). The theme validator already forbids remote fonts; CJK fallback stays a theme concern (doc 27).
- Scale: body 15/22 on phone and 14/20 on desktop; small 12; headings 22 and 18.
- Spacing on a 4 px grid: 4, 8, 12, 16, 24, 32. Radii 6 on controls, 10 on cards and sheets (18 on bottom sheets), full on avatars and chips.
- Density: comfortable by default on both; compact is a per-person setting.

### Motion language: Fluid [PROPOSED]
- Principle: motion explains cause (a sheet rises from what opened it; a pane enters from the side it lives on), tracks the finger while one is down, and never blocks input. Every animation is interruptible and keeps the current velocity when interrupted.
- Only opacity and transform animate (compositor-friendly). Nothing animates at rest.
- Springs are the default curve on phone: critically damped to lightly under-damped. Initial tokens (to be tuned on devices): message arrival 320 ms, sheet 420 ms, pane and chat swipe settle 340 ms. Desktop uses about half the duration with no overshoot: hover 80 ms, palette 120 ms, tab cross-fade 120 to 180 ms.
- Phone set: swipe between chats and panes with the neighbour following the finger; predictive back; row swipe with commit tick; radial sectors that scale and highlight under the thumb with a haptic tick per sector; sheet drag; new messages sliding in with only the new row moving; presence and unread chips easing; list reorder (Kanban card move) as a lifted card with neighbours easing aside.
- Desktop set: palette drop-in, which-key fade after the leader delay, pane tab cross-fade, docking ghost outline, no travel on hover, focus ring moves and does not animate colour.
- **Reduced motion:** when the OS setting or the `reducedMotion` token is on, there is no travel, no spring and no parallax; changes cross-fade in 90 ms or less. Haptics remain.
- Live data never animates a whole list.

### Foldable unfold transition and postures [PROPOSED; owner request 2026-11-22]
- **Unfold animation.** Opening the phone moves the window from the cover screen to the inner screen. The client animates from the folded layout to the unfolded one with the Fluid tokens instead of snapping: the chat the person was reading keeps its place and grows into the chat or ticker slot, the new panes slide and fade in from the hinge side, and the chat-name strip eases into the pane pager. Folding reverses it. State continuity is required: the same view models, scroll position, draft text, selection and open sheet survive the move (doc 41 4.8 already says pane view models survive). Reduced motion cross-fades in 90 ms or less.
- **Driven by the hinge.** Where the platform reports the fold angle, the transition tracks it (progress 0 to 1) so it follows the hands; otherwise it plays on the posture change. This needs the angle from the WindowManager bindings, which is unverified (spike, doc 35).
- **Posture-aware layouts.** Docs 27 and 29 define folded, unfolded and half-open. This doc adds three named layouts, all picked locally from variants the server already sent, with no round trip:
  - **Dual-pane** (flat, fully open): the doc 29 unfolded layout; the chat ticker or list beside the pane pager.
  - **Book** (half-open, vertical hinge): the two halves are two pages. Left half the chat, right half the current pane, with the hinge gap kept clear of content.
  - **Tabletop** (half-open, horizontal hinge): top half shows the content (chat, diff or Constellation), bottom half the controls (composer or approval card and the radial hit area).
- Posture comes from `WindowLayoutInfo` as today (`Posture` record plus hinge orientation). If a company has no variant for book or tabletop, the client uses the half-open variant (`android.half_open_layout`), then folded. Whether book and tabletop become separate server variants or one half-open variant with an orientation flag is open (question 7).
- Content never straddles the hinge: the gap is a layout inset, not a drawn surface, and translucent materials stop at it.

### Liquid motion language [PROPOSED; owner direction 2026-11-22]
Owner direction: a "liquid" feel made of a couple of hundred small details, not large set pieces. Mostly on phone; desktop is subtler. A reduced-animation mode exists on phone. This sits on top of the Fluid tokens above and replaces nothing in them.
- **Four ideas, reused everywhere.** (1) *Droplet spawn*: new things start as a small drop at their origin and expand into shape. (2) *Surface tension*: nearby shapes bulge toward each other and merge or split (metaball effect), then relax. (3) *Viscous spring*: movement stretches along its direction while moving and settles with one small wobble (squash and stretch, volume kept). (4) *Ripple*: touch sends a soft ring through the surface.
- **Three levels,** a setting `client.motion_level`: `liquid` (phone default), `subtle` (desktop default: same ideas at about half the distance and duration, no merge or wobble), `reduced` (fades of 90 ms or less, no travel, no squash; also what the OS reduced-motion setting selects). Level is per person; `reduced` is always reachable on phone in one tap from settings.
- **Budget rules.** Merge and goo effects run only while a small area animates (a bubble, a chip), never on a scrolling list or full screen. Each effect is skipped when the frame budget is missed, falling back one level for that effect. Text is never inside a merge effect; it fades in after the shape settles so it stays sharp. Shapes keep their volume (squash on one axis stretches the other).
- **Implementation [spike, unverified].** Squash, stretch, ripple and droplet travel are plain transforms and clip animations in Avalonia. The metaball merge needs a threshold-blur effect; whether Avalonia 12's composition layer and Skia on Android and Linux can run it cheaply is not known. If not, merge falls back to overlapping shapes with the squash and stretch only. A prototype of the droplet bubble, ripple, typing blobs and viscous switch was shown to the owner in the options page; it was a browser mock, not a measurement.

#### Micro-interaction catalogue [PROPOSED]
Level columns: L = liquid (phone), S = subtle (desktop), R = reduced. "fade" means a 90 ms cross-fade.
| Interaction | L (phone) | S (desktop) | R |
|---|---|---|---|
| New message bubble | Drop leaves the composer or avatar, merges, expands into the bubble with one overshoot; text fades in after | Scale from .96 and fade, 160 ms | fade |
| Own message sent | Drop rises from composer into the list; composer clears with a downward drip | Slide 8 px and fade | fade |
| Typing indicator | Three drops that merge and separate | Three dots, small bounce | dots fade in turn |
| Touch ripple on any pressable | Soft ring from the finger, 900 ms | Hover tint, 80 ms; click tint 200 ms | 90 ms flash |
| Switch toggle | Knob stretches while travelling, wobbles once, track tints as it passes | 140 ms slide | instant |
| Checkbox and radio | Fill spreads from the tap point | Fill fade | instant |
| Button press | Squash to .96 with ripple, release springs back | Darken | darken |
| Send button | Fills like a drop when the composer has text | Fade to accent | fade |
| Unread badge and counts | Pops in as a drop, number rolls | Fade | fade |
| Presence dot change | Dot swells and settles | Colour fade | colour change |
| Reaction chip added | Merges out of the message edge into place | Fade in | fade |
| Reaction chip count change | Number rolls with a small bulge | Fade swap | swap |
| Chat swipe | Neighbour follows the finger; edge bulges toward the finger | Cross-fade on tab switch | fade |
| Pull to load history | Rubber band stretches into a drop that releases | Spinner-free progress line | progress line |
| Row swipe (approve, reply, archive) | Row stretches, action blob grows behind it, commit tick, blob pops | Hover toolbar | fade |
| Bottom sheet | Rises with a meniscus on the top edge, drags with the finger, snaps with a small wobble | Dialog fade and 6 px drop | fade |
| Radial menu open | Drops spread from the touch point into sectors; sector under the thumb swells and the others dim | n/a (mnemonics) | sectors appear, no travel |
| Radial sector change | Highlight blob flows to the new sector, haptic tick | n/a | highlight jump |
| Dictation active | Centre pulses with the voice level as a breathing blob | Level meter bar | static ring |
| Dictation to composer | Blob flows up into the text field | Fade | fade |
| Approval card appear | Drops in from the top strip | Fade and 6 px drop | fade |
| Approve commit | Button fills the card, card dissolves into a ripple, confirmation check draws | Check fades in | check |
| Deny | Card drains downward | Fade | fade |
| Kanban card lift and move | Card lifts with a bulge, neighbours part like liquid, lands with a ripple | Ghost outline, neighbours slide | ghost outline |
| Kanban state change by server | Card flows to the new column | Fade out and in | fade |
| Presence and list reorder | Rows ease past each other | Short slide | none |
| Constellation node focus | Node swells, links ripple outward | Node highlight | highlight |
| Tab and pane change | Content flows in from the side with an edge bulge | Cross-fade | fade |
| Unfold and fold (foldable) | Layout stretches across the hinge like a film, panes bud off the chat | n/a | cross-fade |
| Theme switch | Colour spreads from the toggle as an expanding drop | Cross-fade | instant |
| Toast and banner | Drops from the top and absorbs back | Fade | fade |
| Connection state chip | Chip breathes while connecting; flashes a ripple when live | Colour fade | colour change |
| Loading skeleton | Slow liquid shimmer | Plain shimmer | static |
| Empty state | Single drop idles with slow breathing | Static | static |
| Scroll to bottom button | Rises as a drop; unread count pops | Fade | fade |
| Text input focus | Underline spreads from the caret | Underline fade | instant |
| Error shake | Surface shudders with a ripple | Border flash | border flash |
| Haptics | Tick on commit points, per radial sector, send, approve | none | kept |
The catalogue is the starting set (about 40). The target is to give every pressable, list change and state change a row here as the screens are built; each new row names its three levels and has a headless test that the level switch works. Adding rows is a docs change, as with the doc 29 scope table.

### Everything is a Rubedo-overridable token [PROPOSED; owner note 2026-11-22]
The GUI is fully customisable through Rubedo (docs 41, 41a, 41c), so nothing in this doc is hard-coded in a view. Colours, the hue family, materials, type, spacing, radii, durations, spring parameters, motion level, each micro-interaction's three levels and each gesture threshold (hold time, edge inset, swipe commit distance) live as typed values in theme and motion documents that a company or person can override through the normal scopes. Views reference tokens by name only. A catalogue row is a named interaction preset (`motion.interaction.<name>` with `liquid`, `subtle` and `reduced` variants) that a document can replace or turn off. A headless test scans views for literal colours, durations and thresholds and fails the build on any. Safety floors stay: text contrast, the reduced-motion substitution and the system back-gesture edge cannot be overridden below their floor.

### Theme file additions [PROPOSED]
Additive to schemaVersion 1 (doc 27), under a new schemaVersion 2 with the old tokens unchanged: `color.hue.0` to `color.hue.11` (or the generator parameters), `material.mode`, `material.tint`, `material.opacity`, `material.fallback.*`, `motion.duration.*`, `motion.spring.*`, `motion.scale.desktop`, `motion.liquid.*` (squash and stretch limits, ripple size and duration, merge softness), `layout.sidebar.width.expanded`, `layout.sidebar.width.collapsed`, `layout.strip.height`, `layout.sheet.width`, `layout.sheet.side.width` (device-independent pixels, 0 to 1024), `material.scrim.opacity` (0 to 1) and `gesture.sheet.dismiss.fraction` (0.05 to 0.95; the share of a sheet a drag must pass to dismiss it), plus the existing `reducedMotion`. The validator rejects unknown tokens as it does now. Migration of v1 files fills defaults.

### Settings [PROPOSED]
| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `client.theme` | enum void, graphite, paper, or imported | void | person | person | no |
| `client.density` | enum comfortable, compact | comfortable | person | person | no |
| `client.materials` | enum auto, translucent, opaque | auto | person | person | no |
| `client.motion_level` | enum liquid, subtle, reduced (OS reduced-motion forces reduced) | liquid on phone, subtle on desktop | person | person | no |
| `android.haptics` | bool | on | person | person | no |
| `android.gesture_edge_dp` | int | 24 (floor 16) | install | operator | no |

## Behaviour and edge cases
- A window moved between displays keeps its theme and re-evaluates material support.
- An unsupported backdrop silently uses the next fallback and logs once.
- Theme change applies live with a cross-fade (or instantly under reduced motion) and does not restart panes.
- A gesture started over a text input that selects text does not start a swipe.
- The four visual settings are read from the head's client settings file (`Bazaar.Client.Settings`, a JSON file with registered keys, safe defaults and change notification) and applied live; an unreadable file gives the defaults. `imported` themes fall back to the default until theme import is wired.
- The OS reduced-motion flag is read by each head (Windows `SPI_GETCLIENTAREAANIMATION`, Android `animator_duration_scale` of 0, Linux GNOME `enable-animations` through `gsettings` where present, else not reduced) and forces the reduced level. Heads refresh it when the window activates or the app resumes.
- Frame budget (no graphics settings): the client measures the gaps between frames of running animations only, so measuring costs nothing at rest. When the p95 over a rolling window exceeds 1.5 times the 60 Hz budget it drops blur and caps motion at subtle; past 3 times it caps motion at reduced. It steps back up one level at a time only after several windows under 1.15 times the budget (hysteresis), and after a long pause in animation.
- Fold and unfold animate between variants with the same Fluid tokens and send nothing on the wire (doc 27); see the unfold transition section.

## Testing
- Pure tests: `HueFamily` golden values per theme; theme validator with translucent surfaces at worst-case backdrop; v1 to v2 theme migration; motion token parsing and the reduced-motion substitution; gesture state machine (swipe vs hold vs scroll, edge exclusion, interruption with velocity).
- Headless: each theme loads and every required token resolves; materials fall back to opaque when the request fails; reduced motion turns springs into cross-fades; TalkBack-style custom actions move the pager.
- Frames: baseline captures per theme at phone and desktop sizes (doc 27 frame rules).
- Device checklist (doc 34, not CI): Windows 10 and 11 backdrop behaviour, Linux compositors, Android predictive back on 14 to 16, unfold and fold mid-scroll with a draft open, book and tabletop postures, 120 Hz swipe, blur cost, TalkBack with gestures, haptics.

## Work packages
1. Theme schemaVersion 2 and migration, `HueFamily`, validator for translucent surfaces. Depends on doc 27 theme code.
2. Void, Graphite and Paper theme files and the Avalonia and Monaco generators.
3. Materials service with fallbacks (Windows, Linux, Android), setting and reduce-transparency handling.
4. Motion token set and a Core animation helper (spring, interruptible, velocity-preserving), reduced-motion substitution, and the liquid effects library (droplet, squash and stretch, ripple, merge spike) with the catalogue's headless level tests.
5. Phone gesture layer: pager swipe, row swipe, sheet drag, radial highlight and haptics, accessibility actions.
6. Predictive back spike and `IBackGestureSource`.
7. Apply to screens (shell, GC view, Kanban, dashboard). Depends on the shell and pane work.
8. First-run coach.
9. Unfold transition and posture layouts (book, tabletop). Depends on doc 29 package 2 and 3.

## Open questions
Answered with the recommended option; the owner approved the direction on 2026-11-22 and can change any of these.
1. Colour: golden-ratio hue stepping, as the Constellation does. Any exact palette is a Rubedo override.
2. Default theme: Void. Follow the OS light or dark setting only if the person turns that on.
3. A company layout may set the default theme and material mode; the person can override it.
4. Android blur: allowed when measured cheap, dropped automatically otherwise (no graphics setting).
5. Desktop motion: the half-length subtle set, not phone-level liquid.
6. Tablet and desktop-mode Android behaviour, iconography, empty and error states, and the dashboard layout are a later round.
7. Book and tabletop: one half-open variant with an orientation flag, until a company needs separate ones.
8. Merge (goo) effect: degrade automatically, in line with the owner's no-graphics-settings principle. The client measures frame time, drops the merge effect and then other effects one level at a time, and restores them with headroom. `reduced` stays a person's choice in `client.motion_level`.
