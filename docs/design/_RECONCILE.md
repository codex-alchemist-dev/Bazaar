# Reconciliation instructions (read first)

The 36 design drafts in `docs/design/` were written in parallel and contradicted each other. The owner has since resolved all 45 cross-cutting conflicts. Your job: bring ONE doc in line with those decisions. You edit only the doc named in your assignment.

## Read

1. `/home/user/Bazaar/docs/design/DECISIONS-LOG.md` in full. It is authoritative: where your doc, another doc, `Bazaar.*.md` in `docs/deep-dives/` or older text disagrees with it, the log wins.
2. `/home/user/Bazaar/docs/design/CONFLICT-REGISTER.md`: read every entry (C01 to C45) whose "Docs" line lists your doc's number, to see exactly what conflicted and what the doc must change. Also read the "Stale lines" section for lines concerning your doc.
3. `/home/user/Bazaar/CLAUDE.md` (decisions in force) and `/home/user/Bazaar/docs/design/_BRIEF.md` (skeleton and rules, including the doc index with exact file names).
4. Your doc.
5. Only if needed: the doc(s) that your doc exchanges contracts with.

## Edit your doc in place

- Conform every section to the decisions: tables, interfaces, flows, work packages, testing, and cross-references. If a decision removes a feature or changes an owner (for example table ownership, C04), delete or move the affected text rather than leaving contradictions.
- Do NOT invent new product decisions. If a decision leaves a detail open (the log marks these FOLLOW-UP), keep it as an OPEN question or a [PROPOSED] default.
- Keep what was already right: do not rewrite sections the decisions do not touch.
- Fix the 'Depends on' and 'Used by' lines to use the exact file names from the index.
- Questions for the owner: remove questions the log has answered, or turn them into one line `ANSWERED: <short answer> (DECISIONS-LOG round N)`; keep and renumber the rest; add new questions only for genuinely new open details your edits expose.
- Conflicts section: list only conflicts that REMAIN (including new ones you found, and conflicts with other docs that those docs must fix). Resolved conflicts go away.
- Status line: `Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: <count>`.
- Stay within about 2,000 words (the existing docs run up to roughly 2,600 words; trim sketches before trimming decisions or questions).
- No secrets, credentials, real hostnames or personal data. Keep verified / secondary / unverified labels as they are; never upgrade a claim.
- Edit with your edit tool or by rewriting the file. Do not touch any other file. Do not commit or push.

## Report back

Reply with: the file path, a bulleted list of the substantive changes you made (by decision/round), any decision you could not apply and why, and every NEW conflict you noticed that another doc must fix (give that doc's number and the exact claim).
