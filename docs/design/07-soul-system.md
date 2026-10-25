# Soul System

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: `Bazaar.Server.md` (gateway, taint tracking, GitHub Apps), `Bazaar.Constellation.md`, Soul Passport doc (not yet written) · Used by: agent runtime (Wave 2), Passport doc, Identity Service (PROPOSED)

## Purpose
The Soul is an agent's complete identity: what makes it itself and different from its peers. This doc defines the Soul record and its versions, how personality-driven judgments are audited, and how agents are onboarded, created, promoted, retired and exported as presets.

## Decided inputs
- Soul = VM, personality, memory, reputation, history, accounts, learning; it drives real behavioural differences (`Bazaar.Soul.md` §1).
- Soul data lives host-side, outside the VM disk (`Bazaar.Soul.md` §2). The mount rule is contested (Conflicts 1).
- Passport is a separate credential registry, never part of a Soul export (`Bazaar.Soul.md` §1).
- Personality-driven judgments need auditable reasoning, not only outcomes (`Bazaar.Soul.md` §1; `Bazaar.md` §5).
- Each agent owns its memory, lessons, preferences and reputation. No shared brain (`Bazaar.md` §5).
- Per-agent model whitelists; no global `auto/*`; juniors and Charles use `custom/*` over a cheap whitelist (`CLAUDE.md`; `Bazaar.md` §3, 2026-10-24).
- Two VMs per agent, Windows and Linux (CHANGED 2026-10-24; sizing OPEN). One GitHub App per agent (`Bazaar.Server.md` §2). Email on the operator's domain (`Bazaar.md` §2).
- Taint tracking with a human gate on high-impact actions (`Bazaar.Server.md` §13).
- Every Soul onboards with the Company Constitution (PROPOSED, not drafted).

## Design
Everything below is [PROPOSED] unless tagged DECIDED.

**Soul record parts**

| Part | Holds | Storage | Exported |
|---|---|---|---|
| Identity | name, role, avatar, GitHub App id, email address, timezone, schedule | Soul row | name, role, schedule |
| Charter | personality doc and prompt layers (Constitution ref, role, personal) | `soul_versions` | yes |
| Skills | Agent Skills folders, specialty tags | refs, reputation evidence | skills yes; tags self-declared |
| Preferences | structured style and tool choices | versioned JSON | yes |
| Lessons | append-only entries with source GC seq and trust tag | `lessons` | curated only |
| History | pointers into the GC log, no copies | refs | no |
| Model policy | ref to the gateway whitelist | ref | suggested whitelist, never a grant |
| VM pair | Windows and Linux VM ids | refs | no |
| Accounts | Passport entry ids and labels | refs | no |
| Reputation | derived view from the server | refs | no |

**Versioning.** Charter and preferences change only by appending a version (number, parent, SHA-256, author, reason, decision ref). Nothing is overwritten. Every message and judgment records the `soulVersion` it was produced under. Writes carry an expected version; a stale edit is rejected.

**Auditable judgments.** A personality-driven judgment is a decision record posted to the GC as an immutable message and indexed in a table:

```json
{"decisionId":"d_...","soulId":"s_...","soulVersion":7,"gc":"gc_...",
 "inputs":[{"seq":1042,"trust":"untrusted"}],
 "options":["..."],"chosen":"...","reasoning":"...","confidence":"medium"}
```

Reasoning is required, and the trust tag of every input is logged (threat model §4-5).

**Onboarding.** A new Soul reads the Constitution (version hash recorded), then its role charter, then only lessons the owner has approved. It posts an acknowledgement in its GC. Blocked until the Constitution exists.

**Default roster (presets).** Presets are data files: a Soul template with placeholder identity. Shipped as examples; operators edit or replace them.

| Preset | Role | Model lane | Notes |
|---|---|---|---|
| Claude architect | architecture, review, hard reasoning | Anthropic API key; Haiku/Sonnet switching DECIDED; Opus, Fable OPEN | |
| Gemi research lead | research, docs, planning, PM | install's choice | |
| DeepSeek implementer | heavy implementation | V4 Flash, V4 Pro, 1M-context variants | free supply not guaranteed |
| Charles boilerplate | templates, scaffolding | `custom/*` cheap whitelist | replaces `auto/cheap` (`Bazaar.md` §3) |
| ChatGPT planning | planning, systems input | OPEN | role TBD; read-only reviewer proposed |
| Juniors | specialists | `custom/*` cheap whitelist | specialise from lessons and reputation |

**Creation.** A Talent Pool template is a preset without identity or lessons. Instantiating it creates a Soul with a new GitHub App, email address, child VM disk off the golden image, and empty memory.

Genetic Memory Transfer: a temporary subagent writes candidate lessons to a staging area tagged with its own trust level. Candidates enter a Soul only after review (architect preset or owner). Nothing transfers automatically.

**Promotion.** Specialty tags may be proposed from reputation evidence; the owner approves. A change to a model whitelist or tool grant is a Tier 2 approval, never a side effect of promotion.

**Retirement.** States: active, paused, retired. Retiring revokes Passport entries and installation tokens, deletes the child VM disk (durable data is host-side), and keeps the Soul record and history read-only.

**Export (optional Soul Marketplace), format only.**

```
manifest.json   schemaVersion, bundleVersion, creator, files{path: sha256},
                signature, license (default: none granted), exclusions[], extensions{}
charter.md
preferences.json
skills/<name>/SKILL.md
```

Excluded: Passport, VM disks, GC messages, Vault content, Kanban, raw embeddings, private memories, reputation, and any text naming a person. Export runs a secret-pattern scan and human review.

**Import.** Quarantined. Verify digests and signature. Never execute `scripts/`. Drop imported grants, whitelists and reputation. Show the charter diff to the owner before activation. A valid signature proves origin and integrity, not safety.

**Portable formats.** No standard covers a full Soul. Agent Skills (SKILL.md, verified spec) fits the skills part. A2A Agent Cards are discovery metadata, not identity; use only if agents are exposed over A2A. MCP covers tools, not identity. Open Agent Specification is secondary-sourced only; AGENTS.md was not researched. The manifest is Bazaar-specific JSON.

## Behaviour and edge cases
- Agents may propose charter changes; applying one needs owner or architect approval. Memory, import or GC text never edits the charter directly (OWASP LLM01).
- Quarantined Souls keep visible, tagged messages; their lessons are not promoted.
- Retiring a Soul with an open decision marks it abandoned, never deletes it.
- Reputation is a server-side derived view. Weights OPEN; LLM peer reviews must not dominate promotion.

## Testing
- Real Postgres: version chain, expected-version rejection, immutable decision rows, retirement state.
- Fake gateway: an out-of-whitelist model call is refused.
- Export round-trip; secret scanner fails a planted key; a bundle with tool grants imports with grants empty.
- Malicious import fixture: `scripts/` not run; injected charter text lands in quarantine.
- Retirement: Passport refs revoked, history still readable.
- Avalonia.Headless: profile card shows version and an "edited" marker.

## Work packages
1. Contracts: Soul, SoulVersion, DecisionRecord, Preset, Manifest types and schema. Needs plan WP1.
2. Postgres: `souls`, `soul_versions`, `decision_records`, `lessons` migration. Needs 1 and plan WP2.
3. Versioning service with expected-version checks. Needs 2.
4. Decision record writer and validator. Needs 2.
5. Preset loader and validator; default roster as data. Needs 1.
6. Lifecycle state machine with revoke hooks (interfaces only). Needs 3.
7. Talent Pool and Genetic Memory Transfer staging with review gate. Needs 3, 6.
8. Bundle export: manifest, digests, secret scan. Needs 1, 3.
9. Import quarantine and verifier, with a fake signer. Needs 8.
10. Profile card and version history in `Bazaar.Client.Core`. Needs 3.

## Conflicts
1. **Passport mount.** `Bazaar.Soul.md` §1 keeps the Passport outside the VM; §2 mounts Soul data as one scoped folder, and the owner accepted that with the risk. The threat model and gaps report say never mount the Passport or audit log. This draft assumes the research rule (memory only, read-only), which overrides a DECIDED item and needs owner sign-off.
2. **Marketplace status.** `Bazaar.md` marks the marketplace DECIDED in nature and says company Souls are never shared cross-instance. `Bazaar.Soul.md` marks it PROPOSED. The gaps report says keep it out of v1 or write a carve-out.
3. **Routing table.** `Bazaar.md` §3 still lists `auto/cheap` and `auto/*`, contradicting the no-global-auto rule. Fix in the same change.
4. **Claude lane.** Anthropic API keys only (DECIDED) conflicts with Claude reachable via FreeLLMAPI (DECIDED). FreeLLMAPI maps Claude names to its free pool and documents no key pass-through.
5. **Reputation.** Peer review plus outcomes is DECIDED, weights OPEN. The coordination note reports LLM reviewers can be gamed; the research recommends anchoring to outcomes.
6. **Status drift.** Talent Pool, Genetic Memory Transfer, Identity Service and Constitution are in the v1 list but PROPOSED; the Constitution is undrafted yet onboarding depends on it. The reader/actor split is OPEN in `Bazaar.Server.md` but recommended DECIDED in the gaps report.

## Questions for the owner
1. Passport mount: adopt "Passport and audit never mounted; memory read-only" and amend Soul §2? Default: yes; the research and threat model agree.
2. Marketplace in v1? Default: no; the format is designed, not built.
3. Constitution v1 seed: who writes it? Default: you write a one-page seed; Claude drafts the structure.
4. ChatGPT's role? Default: read-only planning reviewer, no write tools, until you decide.
5. Confirm `custom/*` cheap whitelists for Charles and juniors and remove `auto/*` from `Bazaar.md` §3? Default: yes.
6. Reputation starting weights? Default: promotion uses outcomes only; peer reviews only suggest specialty tags.
7. Does promotion to a new specialty need your approval? Default: yes; model and tool changes always do.
8. Do Genetic Memory Transfer candidates need your approval? Default: architect for lessons, you for charter changes.
9. How long do retired Souls keep readable history? Default: indefinitely, read-only, no tool grants; erasure per Question 12.
10. Decision records: GC message type, a table, or both? Default: both; the GC message is the record, the table is the index.
11. Do judgments pin the exact charter version? Default: yes.
12. Erasure of personal data in lessons and memories, and whether edited messages keep every version? Default: per-person encryption with key destruction, subject to counsel.
13. Export signing: which identity and tool? Default: the exporting agent's GitHub App identity plus an operator key; Sigstore, minisign or Ed25519 unverified.
14. Default licence for exported Souls? Default: none granted until the author chooses. Needs counsel.
