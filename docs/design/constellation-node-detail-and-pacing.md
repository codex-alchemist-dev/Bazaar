# Constellation.Renderer: node icons, sections and frame pacing

Status: DECIDED for the four asks (owner, 2026-11-20); the numbers and shapes below that the owner did not state are PROPOSED or OPEN · Extends: constellation-scale-and-lod.md (section 11, the adaptive budget, quality first) and constellation-render-core.md (section 7, the night-sky theme) · Related: 18-vault.md and constellation-memory-graph.md (wiki-links)

## 1. Scope and who owns what

**Source.** Owner messages, 2026-11-20, relayed by the coordinator. The owner was explicit: the standalone shared module is **Constellation.Renderer**. The Constellation module stays in Bazaar and is the consumer. Only the renderer is extracted.

So everything here is split in two:
- **Renderer** (generic, no Bazaar names, in `Core/` under the boundary rules of constellation-render-core.md): node kinds, icons, level of detail, section rectangles as a drawing primitive, the frame pacer.
- **Bazaar adapter** (the Constellation module): decides what a node is, what a section contains, and where connections come from.

## 2. The four asks [DECIDED, owner 2026-11-20]

1. **This is not a video game.** There are no graphics settings. Everything is dynamic and measured. [DECIDED]
2. **Real icons, not dots.** Every node has an actual icon showing what it is: a document, an idea, a moodboard note sitting in open space that shows its text directly, a label, and whatever else an Obsidian vault plus moodboard needs. Zooming out, the detail is useless, so a node turns into a dot or square (whichever shape works best). The level of detail keeps changing until, far enough away, everything is a dot. The levels and cutoffs come from the performance being measured right now, not fixed numbers. [DECIDED]
3. **Sections.** Close up, large semi-transparent labelled squares enclose several nodes to mark them as an explicit group. Connections can still cross in or out. Groups and connections come from what is written inside the documents, as in Obsidian. Notes with no internal data (open notes and similar) are connected through hidden node metadata. Documents connect only through real content references (a link in the text, being linked from another document, or a sticky note's metadata). [DECIDED]
4. **Efficiency.** Refresh on demand: nothing is drawn while still (0 fps), and drawing starts at once when a drag button or other viewport input begins. The frame rate is dynamic: the target is the display's maximum refresh rate, not a fixed 300. Full rate is only for big movements; small movements run lower so the picture can be better. Example, 120 Hz display: still 0 fps, small movement about 90 fps, big movement 120 fps. On the owner's GPU a big movement wants the full rate, so the level-of-detail cutoffs get more aggressive to hold it. VRR and G-Sync are supported. [DECIDED]

The example numbers (90 and 120 on a 120 Hz display) are the owner's illustration of the idea. The exact small-movement fraction is a setting (section 6, OPEN).

## 3. Node kinds and icons

**Node kind (renderer enum, PROPOSED).** A small closed set the renderer understands. The adapter maps its own types onto it; the renderer never learns Bazaar words.

| Kind | What it shows up close |
|---|---|
| `Document` | A page icon with the title; the first lines when very close |
| `Idea` | A bulb or spark icon with a short title |
| `NoteInSpace` | A moodboard note: its text drawn directly on the canvas, no icon needed |
| `Label` | Plain text only, no body |
| `Image` | A thumbnail |
| `Group` | Reserved for section frames (section 4), not a normal node |
| `Generic` | A fallback shape for anything the adapter has no kind for |

Open to more kinds without a format break: the kind is a small integer in the node style table, and unknown values draw as `Generic`.

**Icons (PROPOSED).** Vector icons drawn as signed distance fields (SDF) from one shared atlas. This keeps them sharp at every zoom, costs one texture read per pixel, and lets colour and glow follow the night-sky theme. A raster atlas is the fallback for icons too detailed for SDF (thumbnails always come from raster tiles). Text for `NoteInSpace` and `Label` uses the same glyph SDF path, so there is one text route.

The night-sky theme still applies: icons are drawn with the same additive glow, and the theme may recolour them. Nothing is hard-coded.

**Level-of-detail ladder per kind (PROPOSED).** The same ladder for every kind, with kind-specific content on each rung:

| Rung | Close to far | Content |
|---|---|---|
| L0 | Closest | Full: icon, title, text preview (note text drawn in full for `NoteInSpace`) |
| L1 | Near | Icon and title |
| L2 | Mid | Icon only |
| L3 | Far | Square or dot (best shape per kind, chosen by the theme) |
| L4 | Farthest | Dot, merged into the density field from constellation-scale-and-lod.md |

At L4 every kind looks the same, as the owner asked. Cutoffs are screen size in pixels at which a rung gives way to the next.

**Performance-driven cutoffs (extends `FrameBudgetController`).** The controller in section 11 of the scale doc already moves a refine threshold and instance cap from measured frame time. The cutoffs here ride on that same loop: when over budget the pixel size at which each rung gives way to the next goes up (nodes simplify sooner), and when there is headroom it comes back down (quality first, as already decided). Text and icon rungs are shed before dots are removed, and the controller never drops frames to keep detail. Rules carried over: drop fast, recover slowly, hysteresis so the picture does not flicker, and no popping beyond one frame of change.

## 4. Sections

**What the renderer draws.** A section is a rectangle (axis-aligned, 2D; a flat panel in 3D), with a label and an opacity. It is a drawing primitive; the renderer does not know why the nodes belong together.
- **Input:** a list of sections, each with an id, a parent id (or none), a label, a colour, and the ids of its member nodes.
- **Rectangle:** computed by the renderer from the positions of the members, plus padding. It follows the nodes if they move.
- **Nesting:** a section may sit inside another. Parent rectangles are drawn first and are larger; children draw above them. A member belongs to its smallest section for drawing.
- **Look:** large, semi-transparent, with the label at the top edge. It is drawn behind nodes and edges. Edges are never clipped or hidden by a section, so connections cross in and out freely.
- **Cull rules (PROPOSED):** sections appear only when close enough that their label would be readable (label height above a minimum pixel size) and when at least two members are on screen. They fade out as the view zooms out, and are gone by L3. Off-screen sections cost nothing. The same rule as the node ladder applies: the frame controller can raise the fade-out distance when over budget.
- **Section count cap:** bounded per frame and shed by the same controller, smallest first.

**What the Bazaar adapter does, not the renderer.** The adapter derives sections from what is written in the Vault:
- A section is an explicit group in the document data, not a guess from layout or clustering. Where Obsidian-style content defines it (a folder, a front-matter group field, or a note that lists its members with `[[wiki-links]]`), the adapter reads it and passes the member ids to the renderer.
- Connections between nodes are the Vault's real links: a `[[wiki-link]]` in the body that resolves to another document, and the reverse (being linked from another document). This is the same rule as `MemoryEdgeKind.Link` in constellation-memory-graph.md: an unresolved name draws no connection.
- Notes with no internal data (open notes, label-style nodes, moodboard notes) have no text to read links from. Their connections and group membership come from hidden node metadata that the adapter stores next to the node. A sticky note's connections also come from its metadata.
- Documents never get connections invented from metadata. A document connects only through its actual content references.
- Which exact front-matter field or folder rule defines a group is OPEN (section 8).

## 5. Frame pacer

The pacer decides when a frame is drawn and how fast. It is generic and lives in the renderer's `Core/`. It reads three things: viewport input, the display's refresh rate, and the measured frame time.

**States (PROPOSED).**

| State | Meaning | Target rate |
|---|---|---|
| `Idle` | Nothing is moving and nothing has changed | 0 fps: no frames submitted |
| `InteractingSmall` | Input is active and the view is changing slowly | A fraction of the display rate (about 0.75, so 90 on a 120 Hz display), buying higher detail |
| `InteractingBig` | Input is active and the view is changing quickly | The display's full rate (120 on a 120 Hz display) |
| `Settling` | Input stopped but easing or level-of-detail catch-up is still running | Follows the last state, then drops to `Idle` once settled |

**Transitions.**
- `Idle` to `InteractingSmall` or `InteractingBig`: on the first input event (drag button down, wheel, key pan, touch). The wake happens on the input event itself, so the first frame is drawn within one frame time, with no timer or polling delay.
- `InteractingSmall` and `InteractingBig` switch both ways, with hysteresis (a short hold before dropping from big to small, so a rate change does not flicker).
- Any interacting state goes to `Settling` when input stops.
- `Settling` goes to `Idle` when the camera has stopped, the detail level has finished catching up (one more full-detail frame is drawn at the end), and no animation is pending.
- Any state goes to an interacting state on new input.
- Also wakes (without input) when something changes on screen: new tile data arrives, a node moves, or a theme animation that is turned on (twinkle) needs a frame. Twinkle never keeps the pacer awake by itself: with no other change the view is still, and Idle holds.

**Input classification (PROPOSED thresholds, all settings).** The pacer measures how fast the view is changing, not what button was pressed.
- Speed is camera pan in screen pixels per second, combined with zoom change per second (log scale) and, in 3D, orbit angle per second.
- Below a lower threshold the movement is small. Above an upper threshold it is big. Between them the state holds (hysteresis).
- Starting points: pan below about 15 percent of the screen's shorter side per second counts as small, above about 40 percent as big; zoom changes of more than about 2x per second count as big. These are guesses to be tuned on the owner's PC (section 8).
- Instant, discrete jumps (jump to a node, fit to view) count as big for their duration.

**Display rate and VRR.**
- The target is the display's maximum refresh rate, read from the display at start and re-read when the window moves to another display or the mode changes. It is not a fixed 300. With several displays, use the one the window is on.
- On a variable refresh rate display (VRR, G-Sync, FreeSync) the pacer presents when a frame is ready, and uses the present mode that does not force a fixed tick; the small-movement target is the cap, not a lock. On a fixed-rate display, frames are paced to a divisor of the refresh rate when it can (so 90 is not forced on 120 Hz without VRR; the nearest even divisor, 60, or the full rate is used instead).
- When the display rate cannot be read, fall back to 60 and raise the setting only on a measured proof, never a guess.

**Relation to the frame controller.** The pacer sets the target rate. `FrameBudgetController` sets the detail needed to hold it. Small-movement states have more time per frame at their lower target, so the controller can spend it on detail (quality first). Big-movement states have the shortest time per frame, so the LOD cutoffs get more aggressive there. On the owner's GPU, where a big movement wants the full rate, that is the case where detail is shed and the target is held. The ladder rungs do not flicker across a state change, because the controller moves detail slowly.

**What "0 fps" means in practice.** In `Idle` the renderer submits no frame and the swapchain is not presented. The last picture stays on screen. The window system still redraws it for exposure (resize, uncover) as it asks for it, and that is the only thing that wakes the pacer at rest besides input and data changes.

## 6. Settings (not graphics settings)

The owner said no graphics settings. These are not a quality dial; they are operator or developer knobs for the pacer and the controller, with safe shipped defaults, in line with the configurability rule: small-movement rate fraction, the two speed thresholds, hold times, and the section cull size. The user never sees a "low, medium, high" choice.

## 7. Tests to write

Renderer (pure, no GPU unless stated):
- Pacer state machine: every transition in section 5, including wake on input within one frame time, Idle producing zero frame requests over a long simulated still period, Settling reaching Idle, hysteresis (no flicker on a speed hovering at a threshold).
- Input classification: pan, zoom and orbit speeds map to small and big as set; discrete jumps count as big.
- Display rate: read 60, 120, 240; VRR and fixed-rate divisor choice (120 Hz fixed does not choose 90); unknown rate falls back to 60.
- LOD ladder: each rung boundary per kind; at far enough distance every kind is a dot; cutoffs move with a simulated over-budget and headroom series (extend the `FrameBudgetController` tests), including drop fast, recover slowly, and no oscillation.
- Section geometry: rectangle from member positions; nesting order; label placement; cull rules (too small, fewer than two on screen); count cap shedding smallest first; edges are not clipped.
- Node kinds: unknown kind draws as `Generic`; style table layout round trip.
- Boundary tests: new types obey `ConstellationRenderCoreBoundaryTests` (no Bazaar names in `Core/`).
- Device test (offscreen readback, as for the instance buffer): icon atlas sampling at several scales; section blend drawn behind nodes.

Bazaar adapter:
- A document with `[[wiki-links]]` gives connections; a note with no body gives connections only from metadata; a document never gets a metadata-made connection.
- Unresolved links draw nothing.
- Group definition in documents gives the right section membership and nesting; no group, no section.
- Mapping from Bazaar node types to renderer kinds is complete (a test fails when a new type has no mapping).
- A headless view test: still means no frame requests; a drag starts frames at once.

## 8. Open items with recommended answers

1. **Small-movement fraction.** OPEN. Recommend 0.75 of the display rate, from the owner's 90 of 120 example, as a setting.
2. **Speed thresholds for small and big.** OPEN. Recommend the section 5 starting points and tune them on the owner's PC.
3. **Icon format.** OPEN. Recommend SDF vector icons from one atlas, raster for thumbnails only.
4. **Final list of node kinds.** OPEN. Recommend the section 3 list; the owner names any missing kind. A custom kind from the adapter falls back to `Generic`.
5. **How a group is declared in documents.** OPEN. Recommend a front-matter `group` field and folder membership, plus a note that lists its members; the adapter reads them and nothing else. This must match the Vault's parsing rules in 18-vault.md.
6. **Where hidden metadata for open notes lives.** OPEN. Recommend a small sidecar block per node in the Vault (so it is visible to git and survives a rebuild), not Postgres only, because the Vault is the source of truth.
7. **Section drawing in 3D.** OPEN. Recommend flat translucent panels that face the camera, with the same cull rule.
8. **Several displays with different refresh rates.** OPEN. Recommend the display the window is mostly on, re-read when it moves.
9. **Twinkle versus Idle.** Recommend twinkle is paused at Idle (section 5), as it already is with reduced-motion.
10. **Licence and name of the extracted package.** Still not decided (constellation-render-core.md section 5); ask the owner, do not choose here.
