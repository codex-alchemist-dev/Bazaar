# Client visual system, materials and motion (desktop and mobile)

Status: PROPOSED, with owner picks of 2026-11-22 recorded below · Owner questions: 7
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

### Theme file additions [PROPOSED]
Additive to schemaVersion 1 (doc 27), under a new schemaVersion 2 with the old tokens unchanged: `color.hue.0` to `color.hue.11` (or the generator parameters), `material.mode`, `material.tint`, `material.opacity`, `material.fallback.*`, `motion.duration.*`, `motion.spring.*`, `motion.scale.desktop`, plus the existing `reducedMotion`. The validator rejects unknown tokens as it does now. Migration of v1 files fills defaults.

### Settings [PROPOSED]
| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `client.theme` | enum void, graphite, paper, or imported | void | person | person | no |
| `client.density` | enum comfortable, compact | comfortable | person | person | no |
| `client.materials` | enum auto, translucent, opaque | auto | person | person | no |
| `client.motion` | enum full, reduced (follows OS by default) | follows OS | person | person | no |
| `android.haptics` | bool | on | person | person | no |
| `android.gesture_edge_dp` | int | 24 (floor 16) | install | operator | no |

## Behaviour and edge cases
- A window moved between displays keeps its theme and re-evaluates material support.
- An unsupported backdrop silently uses the next fallback and logs once.
- Theme change applies live with a cross-fade (or instantly under reduced motion) and does not restart panes.
- A gesture started over a text input that selects text does not start a swipe.
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
4. Motion token set and a Core animation helper (spring, interruptible, velocity-preserving), reduced-motion substitution.
5. Phone gesture layer: pager swipe, row swipe, sheet drag, radial highlight and haptics, accessibility actions.
6. Predictive back spike and `IBackGestureSource`.
7. Apply to screens (shell, GC view, Kanban, dashboard). Depends on the shell and pane work.
8. First-run coach.
9. Unfold transition and posture layouts (book, tabletop). Depends on doc 29 package 2 and 3.

## Open questions
1. Confirm the Constellation hue approach (golden-ratio stepping) is the intended "colourful like the nodes" look, or supply the exact palette.
2. Should the shipped default theme stay Void, or follow the OS light or dark setting at first run?
3. Does a company layout get to set the default theme and material mode, or only the person?
4. Is blur acceptable on low-end phones if it measures fine, or should Android never blur?
5. How much motion on desktop: the proposed half-length set, or phone-level Fluid everywhere?
6. Tablet and desktop-mode Android behaviour, iconography, empty and error states, and the dashboard layout are not covered here and come in a later round.
7. Book and tabletop: separate server variants, or one half-open variant with an orientation flag?
