# Bazaar.Constellation — Knowledge Graph, Vault & Git Visualization

Part of the Bazaar document family. Siblings: **Bazaar** · **Bazaar.Server** · **Bazaar.Windows** · **Bazaar.Android** · **Bazaar.Linux** · **Bazaar.Soul**

Status legend: **[DECIDED]** / **[PROPOSED]** / **[REJECTED/CHANGED]** / **[RESEARCHED]** / **[OPEN]** — see Bazaar.md for definitions.

---

## 1. The Constellation

- **[DECIDED — newly named]** **The Constellation** is the official name for what was informally called the "Mood Board" — the visual, dynamically-arranging graph layer showing docs/decisions/PRs/research as clusters of orbiting nodes. The Vault (below) remains the underlying document store this visualizes; The Constellation and the Vault are two views of one knowledge graph, not two separate systems. (A standalone "Knowledge Graph" feature was explicitly considered and rejected as redundant — Vault + Constellation already are one.)
- **[DECIDED]** **Reverse Gravity Layout** ("anti-gravity") — the force-directed auto-layout technique The Constellation runs on. The focused document becomes the center; everything else orbits/rearranges dynamically like clustered solar systems branching from central items.
- **[DECIDED]** Vim-style navigation ties directly into this layout — navigating/zooming triggers *real* dynamic restructuring of the graph (a node becoming the new center of its own cluster), not just camera movement.
- **[DECIDED — updated for the Avalonia move]** Rendering implementation: **native, via Skia** — not D3-Force, not WebGPU (both web-only technologies with no place in a native app), and not Win2D either now that the client stack moved to Avalonia (see Bazaar.Windows) — Avalonia's entire rendering pipeline already *is* Skia, so The Constellation's custom force-directed rendering has first-class, direct access to it rather than needing a separate interop layer the way Win2D would have been under WinUI 3. The force-directed layout algorithm itself is small enough to implement directly. Choosing 2D now doesn't foreclose a literal 3D "solar system" visual later — the graph/physics data model is renderer-agnostic, so a 3D renderer could sit on top of the same data without a rewrite, purely for aesthetics if ever wanted.

---

## 2. Git Solar System

- **[DECIDED — naming]** Visual Git interface: branches orbit like planets, commits are celestial bodies, merges appear as comets. Extended beyond Git to cover Vault/document branching too, using the same visual language as The Constellation.

---

## 3. Document Vault

- **[DECIDED]** Every project gets its own Vault with, at minimum: an **Index**, a **Plan**, **Deep Dive** docs per system/API, plus **Research** docs.
- **[DECIDED]** Obsidian-style bi-directional `[[wiki-links]]` between documents.
- **[DECIDED 2026-10-24, owner]** Source of truth: **Markdown files in a git repo** (the private company repo), edited by agents like code. PostgreSQL indexes them for search and for the Constellation graph; it is not the source of truth.
- **[DECIDED]** The Vault is accessible from all relevant GCs.
- **[DECIDED]** Search: **ParadeDB's `pg_search`** — a Postgres extension providing production-ready BM25 full-text search, combined with `pgvector` for real hybrid lexical+semantic search. Avoids adding a separate search service to sync; works automatically for all clients since it's server-side and every client queries the same API. Note: `pg_search` is AGPL-licensed itself, but used via SQL (not linked into Bazaar's own binary) — same relationship as using Postgres itself; a real legal check is still worth doing before shipping open source. **[Updated 2026-10-24]** Search sits behind an interface: `pg_search` on Linux, Postgres full-text search + pgvector on Windows (no documented Windows build of `pg_search`). AGPL obligations may attach if an installer bundles it — needs counsel. Full stack context in **Bazaar.Server**.

---

## Open Questions (Constellation)

None specific to this doc beyond what's tracked in Bazaar.Server (search stack) and Bazaar.Windows (rendering "feel" validation spike).
