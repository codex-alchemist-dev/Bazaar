# Research

This folder holds the research behind the Bazaar design docs in `docs/deep-dives/`.

- `notes/` holds the per-topic research notes. Each subfolder is one research request, and each file in it covers one topic.
- `reports/` holds the synthesized reports. Each report draws on the notes in the matching subfolders and states what the research means for the design.

Claims in the notes and reports carry one of three labels:

- **verified**: checked against a primary source (the page or document was read in the research pass).
- **secondary**: taken from a secondary source, such as a summary, a third-party article or another note.
- **unverified**: not yet checked, or checked without a usable source.

Nothing in this folder is legal advice. Notes that touch licences, terms of service, or law are research for the owner and counsel to review, not a legal opinion.

## Reports

- [Bazaar stack feasibility research.md](reports/Bazaar%20stack%20feasibility%20research.md): tests the .NET stack, Windows hosting, vendor terms and model-supply pillars against primary sources and lists the blockers the owner must decide.
- [Bazaar remaining gaps research.md](reports/Bazaar%20remaining%20gaps%20research.md): finds where the gap research overturns or strains DECIDED items in the design docs and what the owner needs to rule on before implementation.
