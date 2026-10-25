# Soul System

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 12
Depends on: `06-agent-runtime.md` (agent row, lifecycle states, wake), `04-postgres-schema-and-migrations.md` (conventions, key table), `05-model-gateway.md` (whitelists, identity lanes), `08-memory-system.md` (per-agent Vault memory), `09-work-life-simulation.md` (schedule), `14-identity-service-and-passport.md` (Passport, secrets), `15-security-taint-and-approvals.md` (provenance enum, taint, approval tiers, mount detach), `16-agent-vms-and-environments.md` (VM pair), `18-vault.md` (VaultWriter, decision files), `10-council-and-voting.md` (promotion, hiring, retirement votes), `11-reputation-and-peer-review.md`, `01-gc-data-model.md` (judgment messages, history) · Used by: `06-agent-runtime.md`, `08-memory-system.md`, `10-council-and-voting.md`, `14-identity-service-and-passport.md`, `25-company-systems-knowledge.md`, `33-backup-dr-and-export.md`

## Purpose
The Soul is an agent's complete identity: what makes it itself and different from its peers. This doc defines the Soul's parts and versions, how personality-driven judgments are audited, how agents are onboarded, created, retired and exported as presets. The agent row and its lifecycle belong to `06-agent-runtime.md`; this doc owns Soul versions.

## Decided inputs
- Soul = VM, personality, memory, reputation, history, accounts, learning; it drives real behavioural differences (`Bazaar.Soul.md` §1).
- Passport is a separate credential registry, never part of a Soul export (`Bazaar.Soul.md` §1; `14`).
- **Mount (DECIDED, owner, DECISIONS-LOG round 2, C02):** the per-agent scoped folder holds metadata plus a session-cookie profile, and is detached automatically on hard taint (`15`). Memory and reputation go through server APIs and the Vault, never the mount. Canary credentials, a separate share and a separate OS account per agent are required (`15`). The second share in `16` Q2 is an explicit exception.
- Two VMs per agent, Windows and Linux (CLAUDE.md; DECISIONS-LOG C03). Benchmarks size the pair; they do not choose it.
- Agent row and lifecycle states: `06` (C04). Schedule and availability: `09` (C04).
- Memory is a per-agent Vault area; structured state stays in Postgres (C38, round 11).
- Provenance uses `15`'s enum on every input; memory promotion uses `08`'s T0 to T3 tiers (C20).
- Approval ladder: `15` (C21).
- Promotion, hiring and retirement are Council votes. A unanimous vote passes; split or tied bigger changes go to the owner (C32).
- Decision records are Vault files `decisions/NNNN-slug.md`, written only through `VaultWriter` (C24).
- Personal fields are per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone (C01). Legal sufficiency needs counsel.
- Every edit keeps its version with an "edited" marker (CLAUDE.md; C09).
- Claude lane: the Claude member uses both the owner's Anthropic API key and FreeLLMAPI free tokens, by availability; usage records carry `identity: native | pooled`. Subscription logins stay off every route (C14, `05`).
- Per-agent model whitelists; no global `auto/*`. Charles and juniors use `custom/*` over a cheap whitelist with FreeLLMAPI (CLAUDE.md; `Bazaar.md` §3).
- Marketplace: optional, user-generated, behavioural presets only. v1 scope OPEN. Mirror Agent profile and preferences are excluded from exports (`26`).

## Design
Everything below is [PROPOSED] unless tagged DECIDED.

**Soul record parts**

| Part | Holds | Storage (owner) | Exported |
|---|---|---|---|
| Identity | name, role, avatar, GitHub App id, email address | agent row (`06`) | role only |
| Timezone, schedule | work-life settings | `09` | no |
| Charter | personality doc and prompt layers (Constitution ref, role, personal) | version rows here; text as a Vault file (Q12) | yes |
| Skills | Agent Skills folders, specialty tags | refs; reputation evidence (`11`) | skills yes; tags self-declared |
| Preferences | structured style and tool choices | versioned JSON in `soul_versions` | no |
| Lessons | append-only entries with source GC seq, provenance tag and `08` tier | per-agent Vault memory area (`08`) | no (private memory) |
| Memory | persistent notes | per-agent Vault area (`08`, C38) | no |
| History | pointers into the GC log, no copies | refs (`01`) | no |
| Model policy | ref to the gateway whitelist (`05`) | ref | suggested whitelist, never a grant |
| VM pair | Windows and Linux VM ids | refs (`16`) | no |
| Accounts | Passport entry ids and labels | refs (`14`) | no |
| Reputation | derived view from the server | refs (`11`) | no |

**Versioning.** Charter and preferences change only by appending a `soul_versions` row: number, parent, SHA-256 of the content, author, reason, decision ref. Nothing is overwritten. Every message and judgment records the `soulVersion` it was produced under. Writes carry an expected version; a stale edit is rejected.

**Auditable judgments.** A personality-driven judgment is a decision record. It is posted to the GC as an immutable message (`01`) and written as a Vault file `decisions/NNNN-slug.md` through `VaultWriter` (`18`). Reasoning is required, and the provenance of every input is logged:

```json
{"decisionId":"d_...","soulId":"s_...","soulVersion":7,"gc":"gc_...",
 "inputs":[{"seq":1042,"source_trust":"external"}],
 "options":["..."],"chosen":"...","reasoning":"...","confidence":"medium"}
```

**Mount composition [DECIDED content, C02].** The per-agent folder contains service metadata (names, handles, expiry) and the session-cookie profile for allowlisted sites. Nothing else. Hard taint detaches it (`15`); the agent then loses account access until a clean session.

**Onboarding.** A new Soul reads the Constitution (version hash recorded), then its role charter, then only lessons the owner has approved. It posts an acknowledgement in its GC. Blocked until the Constitution exists.

**Default roster (presets).** Presets are data files: a Soul template with placeholder identity. Shipped as examples; operators edit or replace them.

| Preset | Role | Model lane | Notes |
|---|---|---|---|
| Claude architect | architecture, review, hard reasoning | Anthropic API key and FreeLLMAPI, by availability (C14); Haiku and Sonnet whitelisted; Opus and Fable only if the live model list shows them | |
| Gemi research lead | research, docs, planning, PM | install's choice | |
| DeepSeek implementer | heavy implementation | V4 Flash, V4 Pro, 1M-context variants | free supply not guaranteed |
| Charles boilerplate | templates, scaffolding | `custom/*` over a cheap code-capable whitelist when FreeLLMAPI is installed; otherwise a pinned cheap whitelist | replaces `auto/cheap` |
| ChatGPT planning | planning, systems input | OPEN | role TBD (Q3) |
| Juniors | specialists | `custom/*` over a cheap whitelist | specialise from lessons and reputation |

**Creation.** A Talent Pool template (`25`) is a preset without identity or lessons. Instantiating it creates an agent with a new GitHub App, an email address on an operator-configured domain, child Windows and Linux VM disks off their golden images (`16`), and empty memory. It runs as a Council hiring vote (C32).

Genetic Memory Transfer: a temporary subagent writes candidate lessons to a staging area tagged with its own provenance. Candidates enter a Soul only after review (Q5). Nothing transfers automatically.

**Promotion.** Specialty tags and promotions are Council votes (`10`), fed by `11`'s evidence. A model whitelist change or a tool grant change is a Tier 2 approval under `15`'s ladder; whether a whitelist change is a "bigger change" under C32 is Q11. Promotion never changes a whitelist or grant as a side effect.

**Retirement.** Lifecycle states come from `06` (Created, Onboarding, Active, Resting, Retired). Retirement is a Council vote (C32). On retirement this doc's hooks revoke Passport entries and installation tokens (`14`), delete both child VM disks (durable data is host-side), and keep the Soul record and history read-only. An open decision marks the Soul abandoned, never deletes it.

**Export (optional Soul Marketplace), format only.** A bundle is a behavioural preset.

```
manifest.json   schemaVersion, bundleVersion, creator, files{path: sha256},
                signature, license (default: none granted), exclusions[], extensions{}
charter.md
skills/<name>/SKILL.md
```

Excluded: Passport, VM disks, GC messages, Vault content, Kanban, raw embeddings, memories, lessons, preferences, the Mirror Agent profile (`26`), reputation, and any text naming a person. Export runs a secret-pattern scan and human review.

**Import.** Quarantined. Verify digests and signature. Never execute `scripts/`. Drop imported grants, whitelists and reputation. Show the charter diff to the owner before activation. A valid signature proves origin and integrity, not safety.

## Behaviour and edge cases
- Agents may propose charter changes; applying one needs owner or architect approval [PROPOSED]. Memory, import or GC text never edits the charter directly (OWASP LLM01).
- Quarantined Souls keep visible, tagged messages; their lessons are not promoted.
- Reputation is a server-side derived view. Weights are OPEN; LLM peer reviews must not dominate promotion.

## Testing
- Real Postgres: `soul_versions` chain, expected-version rejection, retirement state from `06`.
- Vault fixtures: a judgment writes exactly one decision file through `VaultWriter`; direct writes are refused.
- Fake gateway: an out-of-whitelist model call is refused; a `custom/*` route resolves only to the cheap whitelist.
- Mount: a fixture folder holds only metadata and the cookie profile; a hard-taint signal detaches it; a canary credential planted outside the folder is never visible inside it.
- Export round-trip; secret scanner fails a planted key; planted preferences and Mirror Agent profile are excluded; imports keep grants empty; `scripts/` never runs.
- Retirement: Passport refs revoked, both VM disks deletion requested, history readable.
- Avalonia.Headless: profile card shows version and an "edited" marker.

## Work packages
1. Contracts: SoulVersion, DecisionRecord (judgment), Preset, Manifest types; provenance enum imported from `15`. Needs plan WP1.
2. Postgres: `soul_versions` migration only, per `04` conventions. No `souls`, `lessons` or `decision_records` tables. Needs 1 and plan WP2.
3. Versioning service with expected-version checks. Needs 2.
4. Judgment writer: posts the GC message (`01`) and asks `VaultWriter` (`18`) for the decision file. Needs 2 and `18`.
5. Preset loader and validator; default roster as data, including `custom/*` routes. Needs 1.
6. Retirement hooks (revoke Passport entries and tokens, delete VM disks), called from `06`'s lifecycle. Needs 3, `14`, `16`.
7. Talent Pool and Genetic Memory Transfer staging behind a Council vote gate (`10`). Needs 3, 6.
8. Bundle export: manifest, digests, secret scan, exclusion list. Needs 1, 3.
9. Import quarantine and verifier, with a fake signer. Needs 8.
10. Profile card and version history in `Bazaar.Client.Core`. Needs 3.
11. Mount composition: build the per-agent folder (metadata and cookie profile only) and detach on the `15` taint signal. Needs `14`, `15`.

## Conflicts
Remaining after reconciliation:
1. **Deep-dive text (fix in the same change):** `Bazaar.Soul.md` lines 12, 26, 28 and 54 and `Bazaar.md` lines 55, 58, 60 and 63 still carry the superseded mount, golden-image, topology and routing wording; CLAUDE.md's Claude-lane line still says "API keys only" (stale-lines table, C03, C14).
2. **Marketplace status:** `Bazaar.md` calls it DECIDED in nature; this doc says PROPOSED with v1 scope OPEN (Q2). Align after Q2.
3. **Bigger-changes definition (C32 FOLLOW-UP):** Q11 decides what needs an owner-level vote; until then, whitelist and grant rules are provisional.
4. **For `06`:** its `agent` row carries `schedule jsonb`, but C04 gives schedule to `09`.
5. **For `08`:** this doc no longer owns a `lessons` table; lessons are Vault memory entries (C38). `08` must confirm.
6. **For `10` or `18`:** C24 names one decision index table for judgments and decision files, but no doc owns it.
7. **For `11`:** its automatic promotion at LCB ≥ 0.6 conflicts with C32 (Council vote). It must say so.
8. **FOLLOW-UP (C38):** erasure inside Vault Markdown and git history is unanswered (Q8).

## Questions for the owner
1. Marketplace in v1? Default: no. The format is designed, not built; any bundle is optional, user-generated and behavioural only.
2. Constitution v1 seed: who writes it? Default: you write a one-page seed; Claude drafts the structure.
3. ChatGPT's role? Default: read-only planning reviewer, no write tools, until you decide.
4. Reputation starting weights? Default: `11`'s PROPOSED table; promotion weighs outcomes only; peer reviews only suggest specialty tags. Weights stay OPEN in `Bazaar.md` §5.
5. Genetic Memory Transfer candidates: who approves? Default: the architect preset for lessons; you for charter changes.
6. How long do retired Souls keep readable history? Default: indefinitely, read-only, no tool grants; erasure per Q8.
7. Do judgments pin the exact charter version? Default: yes.
8. Erasure inside Vault Markdown and git history (FOLLOW-UP after C38). Per-person encrypted fields cover Postgres rows only. Default [PROPOSED]: sensitive memory and lesson notes are written as per-person encrypted payloads where the Vault layout allows; no history rewrite; counsel decides whether key destruction suffices.
9. Export signing: which identity and tool? Default: the exporting agent's GitHub App identity plus an operator key; the signing tool is unverified.
10. Default licence for exported Souls? Default: none granted until the author chooses. Needs counsel.
11. Does a model whitelist change count as a "bigger change" (C32 FOLLOW-UP)? Default: yes, a Council vote, with you deciding only on a split or tie; tool grants stay Tier 2 owner approval under `15`.
12. Charter text: Vault file or Postgres row? Default: Vault file, with its SHA-256 in `soul_versions`; `18` to confirm.

ANSWERED: Passport mount is metadata plus a session-cookie profile, detached on hard taint; memory and reputation go through APIs and the Vault (DECISIONS-LOG round 2, C02).
ANSWERED: Charles and juniors use `custom/*` over a cheap whitelist with FreeLLMAPI; no global `auto/*` (reconciliation brief, 2026-10-25; CLAUDE.md).
ANSWERED: Promotion, hiring and retirement are Council votes; the owner decides only split or tied bigger changes; there is no owner approval step for promotion (DECISIONS-LOG round 9, C32).
ANSWERED: Decision records are Vault files `decisions/NNNN-slug.md` written through `VaultWriter` (DECISIONS-LOG round 7, C24).
ANSWERED: Edited messages keep every version with an "edited" marker (CLAUDE.md; DECISIONS-LOG round 1, C09).
