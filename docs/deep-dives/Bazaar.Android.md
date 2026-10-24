# Bazaar.Android — Development Console

Part of the Bazaar document family. Siblings: **Bazaar** · **Bazaar.Server** · **Bazaar.Windows** · **Bazaar.Linux** · **Bazaar.Soul** · **Bazaar.Constellation**

Status legend: **[DECIDED]** / **[PROPOSED]** / **[REJECTED/CHANGED]** / **[RESEARCHED]** / **[OPEN]** — see Bazaar.md for definitions.

---

## 1. Product Identity

- **[DECIDED]** Philosophy: **"Stay connected."** Explicitly named a "Development Console," not a "Mobile IDE" or "Companion App" — that wording sets what it optimizes for. Review PRs, sketch on The Constellation, approve merges, reply to agents, rearrange Kanban, watch agents work, annotate screenshots, record voice ideas, leave comments. "Office in your pocket," not a place to write 500 lines of C++.

---

## 2. Why Android stays functionally distinct — the *why*, not just the *what*

Deliberately logged in full, since it's easy for this reasoning to get lost once the client framework became shared with desktop (Section 3):

- **[DECIDED]** Bazaar.Android stays functionally and philosophically distinct from the desktop clients **regardless of the Avalonia move**. Sharing a rendering engine doesn't erase the different intended use case, and the use case — not the tech stack — is what actually drives the feature differences.
- **Original reasoning (from the DeepSeek conversation):** the physical act of folding/unfolding a device is treated as a deliberate context-switch ritual, not just a responsive-layout breakpoint.
  - **Folded = quick triage/cockpit mode.** "You're lying in bed, you shouldn't be writing 500 lines of C++."
  - **Unfolded = a real but still fundamentally *reactive* multi-pane session** (diffs, task board, background workflows) — not a replacement for sitting down at Bazaar.Windows/Linux to actually build something.
- Same product-philosophy split as always: Windows/Linux exist to **build**, Android exists to **stay connected**. A shared UI renderer is an implementation detail underneath that split, not a reason to erase it — worth watching for in practice, since it would be an easy thing for an agent (or a future contributor) to "simplify" toward desktop feature parity once the code is shared, without realizing that would be undoing a deliberate decision.

---

## 3. UI/UX Specifics

- **[DECIDED]** Fold/unfold-aware layout (target device: a foldable, e.g. Find N6). Folded = full chat view. Unfolded = chat shrinks to an NPU-summarized notification ticker; main space becomes swipeable panes (Diffs / Task Board / Background workflows) with a bottom-drawer Constellation.
- **[DECIDED]** Gesture nav: hold-anywhere brings up a radial menu; **[DECIDED 2026-10-24, owner — clarification]** dictation is the *center* of the radial menu: press and hold anywhere opens the radial; if you don't move to another option, the center — voice dictation — is already active. Holding directly on the chat input goes straight to voice-to-text.
- **[DECIDED]** Voice input pipeline: raw transcription → local Co-Worker/NPU cleanup pass ("whisper-flow" style — removes filler words, reorders rambling into coherent structure) → final text, entirely on-device.
- **[DECIDED]** Collapsed/expandable action logs generated locally via the platform NPU, same as Windows.

---

## 4. Platform Implementation

- **[DECIDED]** Local inference: **ONNX Runtime Mobile + QNN execution provider** (Qualcomm NPU) — same ONNX model format as Windows, avoiding a separate TFLite conversion pipeline. **[RESEARCHED 2026-10-24]** No Android .NET NuGet for the QNN execution provider was found; plan Java bindings or P/Invoke. Working references are single-developer and anecdotal.
- **[RESEARCHED]** Foldable detection: AndroidX **Jetpack WindowManager** (`FoldingFeature`/`WindowLayoutInfo`), accessed via .NET-for-Android bindings. Clarifying the actual layering, since it's easy to conflate: **Avalonia is only the UI renderer** — it sits on top of **.NET for Android** (the runtime + Java/Kotlin interop layer), which is what provides bindings to call the same `androidx.window` library any native Kotlin/Compose app would use for posture/fold detection. Documented as a known, supported pattern (Microsoft has an official guide for exactly this .NET + Jetpack WindowManager scenario), not an edge case or a hack.
- **[RESEARCHED]** Avalonia 12's rebuilt Android backend targets reliable high-refresh-rate scheduling and reduced animation jank — see **Bazaar.Windows** Section 2 for the full framework-choice writeup this belongs to.
- **[DECIDED]** Hardware scope: Qualcomm/Snapdragon NPU only, for now — not building cross-platform NPU support yet given this is currently a solo-user system.

---

## Open Questions (Android)

1. **Foldable-layout validation spike** — recommended alongside the Windows docking spike (see **Bazaar.Windows**), not yet performed. This is the one piece of the original plan hardest to verify from documentation alone.
2. **Android text input (IME) and soft keyboard in Avalonia** — likely the biggest mobile risk; test on a real Android 16 foldable before committing.
