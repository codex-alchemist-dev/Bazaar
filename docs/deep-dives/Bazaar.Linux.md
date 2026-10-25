# Bazaar.Linux — PC Client (low priority)

Part of the Bazaar document family. Siblings: **Bazaar** · **Bazaar.Server** · **Bazaar.Windows** · **Bazaar.Android** · **Bazaar.Soul** · **Bazaar.Constellation**

Status legend: **[DECIDED]** / **[PROPOSED]** / **[REJECTED/CHANGED]** / **[RESEARCHED]** / **[OPEN]** — see Bazaar.md for definitions.

This is intentionally the thinnest doc in the family — the client itself is low priority and largely unscoped, but worth tracking as its own doc since the reasoning for why it exists at all is real and shouldn't get buried in Bazaar.Windows.

---

## 1. Product Identity

- **[DECIDED, low priority]** Philosophy: same as Bazaar.Windows — **"Build products."** A genuine PC-class Linux desktop client, not a stripped-down or secondary experience.
- **[DECIDED]** Added to the roadmap only after the Avalonia move (see **Bazaar.Windows**) made it close to free — since Avalonia renders its own UI via Skia rather than wrapping OS-native widgets, a Linux build is closer to "the same app, different OS" than a real port. Without that move, this client likely wouldn't exist on the roadmap at all.
- **[DECIDED]** Coding order: **Bazaar.Server → Bazaar.Windows → Bazaar.Android → Bazaar.Linux**. Last, specifically because it rides on the shared UI code (`Bazaar.Client.Core`, see Bazaar.Windows Section 3; this head is `Bazaar.Client.Linux`) the other clients establish first — not because it's architecturally hard on its own.

---

## Open Questions (Linux)

1. **Feature scope** — no scope has been defined yet beyond "same as Windows." Worth an explicit pass once Bazaar.Windows is far enough along to know what's actually worth porting versus platform-specific enough to skip (e.g., ConPTY-based terminal work doesn't apply — a native PTY equivalent is needed instead). Monaco hosting on Linux (WebKitGTK) is part of the Avalonia-wide gate spike, which is still OPEN (DECISIONS-LOG round 5, C26; see Bazaar.Windows).
