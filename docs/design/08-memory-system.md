# Agent Memory System

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 5
Depends on: `04-postgres-schema-and-migrations.md` · `05-model-gateway.md` · `06-agent-runtime.md` · `07-soul-system.md` · `09-work-life-simulation.md` · `14-identity-service-and-passport.md` · `15-security-taint-and-approvals.md` · `18-vault.md` · `docs/deep-dives/Bazaar.md` §5 · `Bazaar.Soul.md` §1–2 · Used by: `09-work-life-simulation.md`, `10-council-and-voting.md`, `11-reputation-and-peer-review.md`, `18-vault.md`, `19-constellation-and-git-solar-system.md`, `25-company-systems-knowledge.md`, `33-backup-dr-and-export.md`

## Purpose

Memory is what makes an agent persistent: experience, lessons, failures and preferences, plus the project and company knowledge it may use. Under DECISIONS-LOG round 11 (C38) memory is **Markdown notes in the Vault**: a per-agent private area plus shared project and company areas, written only through the `VaultWriter` of doc 18. PostgreSQL holds the search and embedding index over those files and the structured state around them; it is never the source of truth. This doc covers the note layout, the memory tools, validation against the Soul, consolidation, trust, retrieval, privacy, erasure and the Hive Mind boundary.

## Decided inputs

- Each agent owns private, project and working memory, experience, lessons, preferences, failures and reputation. No shared brain. (`Bazaar.md` §5)
- Almost anything saved or written goes in the Vault, including each agent's persistent memory. The original Soul table lists "Memory Vault: Per-agent persistent memory". (DECISIONS-LOG round 11, C38; verified in the original export per the log)
- Vault source of truth: Markdown with `[[wiki-links]]` in a git repo; Postgres only indexes it. (`CLAUDE.md`)
- Company knowledge is Vault only; there is no `company` memory scope (C38).
- One embedding profile per install, chosen at setup, stored as `halfvec` (C39; profile table in doc 04).
- Two trust scales (C20): doc 15's provenance enum says where content came from; T0 to T3 here are promotion tiers. Mapped at the boundary (section 5).
- Personal fields are per-person encrypted payloads; erasure destroys the key and appends a tombstone (C01).
- GC records are append-only and never purged; edits keep every version. (`Bazaar.md` §2)
- Soul data lives outside the VM disk (`Bazaar.Soul.md` §2); Soul versions belong to doc 07.
- Company Memory and Cross-Project Hive Mind are v1 scope, as Vault areas. (owner, 2026-10-24)
- Task brief, not in the docs (confirm): agents add and edit their own memories; tools are store, retrieve, update, deprecate.

## Design

### 1. Kinds mapped to Vault areas [PROPOSED]

Paths extend doc 18's layout; doc 18 must register the `memory` doc type and these roots.

| Kind | Note `type` | Vault path | Written by | Lifetime |
|---|---|---|---|---|
| episodic | `memory-episode` | `agents/<agent>/memory/episodes/<date>/<ulid>.md` | runtime at turn end, no LLM | Permanent |
| semantic | `memory-semantic` | `agents/<agent>/memory/semantic/<slug>.md` | consolidation only | Until deprecated |
| procedural | `memory-procedure` | `agents/<agent>/memory/lessons/<slug>.md` | agent | Until deprecated |
| failure | `memory-failure` | `agents/<agent>/memory/failures/<slug>.md` | agent | Never deleted; decay floor |
| preference | `memory-preference` | `agents/<agent>/memory/preferences/<slug>.md` | agent; owner | Until deprecated |
| working | `memory-working` | `agents/<agent>/memory/working/<task>.md` | agent | Task close plus TTL |
| project | any memory type | `projects/<slug>/memory/…` | agents of the project | Until deprecated |
| company | procedural, failure or semantic, plus a `company` block (section 6.2) | `company/memory/<company-kind>/<slug>.md`; candidates `company/candidates/<ulid>.md` | proposal or distillation, admitted by the review window, a Council motion or the owner; T3 by the owner only (section 6.4) | Never deleted; review dates (section 6.7) |
| hive | `memory-hive` | `hive/<kind>/<slug>.md` | copy flow (section 6.1) | Until deprecated |

Scopes are therefore areas: `private` (the agent's own `agents/<agent>/`), `project`, `company`, `hive`. There is no separate company scope in the database; a company fact is a note under `company/`, and its tier, evidence and review dates are front matter. Episodic notes are tiny, so a turn summary costs one small file; they are written in batches (section 3). Working notes are deleted by the sweeper at TTL, and git history keeps them. Lessons are Vault memory notes (`memory-procedure`, the procedural kind above); doc 07 has no `lessons` table (cross-doc pass, doc 07 fix list).

Note front matter (doc 18's keys plus memory keys):

```yaml
id: 01J...  type: memory-procedure  title: ...  project: example
memory: {kind: procedural, status: active, version: 3, trust: T2, source_trust: agent,
         tainted: false, origin: {gc: ..., messages: [..]}, derived_from: [01J..],
         subjects: [], deprecated: {reason: ..., by: ..., at: ...}, supersedes: [01J..]}
```

Relations (`derived_from`, `contradicts`, `supersedes`) are front-matter lists and `[[wiki-links]]`, so the Constellation shows them as edges.

### 2. Tools and validation [PROPOSED]

Tools: `memory_store`, `memory_retrieve`, `memory_update` (needs expected version), `memory_deprecate` (needs reason). There is no hard-delete tool. Each is a thin operation on `IVaultWriter` (doc 18) with a memory validator in front:

- `store` creates a note. `update` rewrites a note body or front matter against `expectedVersion`. `deprecate` sets `status: deprecated` plus reason in front matter; the file stays and drops out of retrieval. `retrieve` is a read through the Vault access layer (section 8) and the index, not a write.
- Memory writes go through a **memory write path** of the writer, not the branch-per-task flow of doc 18: the writer commits directly to the vault main branch inside the agent's private subtree, one commit per batch (section 3). Overlapping edits cannot happen across agents there, because one agent owns the subtree. Shared areas (`projects/…/memory`, `company/memory`, `hive/`) use doc 18's normal branch-and-merge flow. This is a NEW request on doc 18 (Conflicts).

SoulGuard runs on every write, before the writer sees it:

1. **Structure:** area and kind allowed for the actor, size (4 KB), provenance present, front matter valid.
2. **Soul core is out of reach:** identity, personality, model whitelist, grants and Passport references cannot be written. Text claiming a role, permission or approval is stored at T1 and flagged; it never changes the Soul (doc 07 owns Soul versions).
3. **Secrets:** pattern scan rejects a match.
4. **Duplicates:** high embedding similarity turns a store into an update.
5. **Conflicts:** stale `expectedVersion` is rejected; conflicting project writes are both kept and linked `contradicts`.
6. **Personal data:** subject-tagged spans are encrypted before the write (section 9).

The validation result goes into the commit message trailer `Bazaar-Memory-Validation` and the `memory_state` row, since git history is the version log; there is no `memory_versions` table.

### 3. Commit batching [PROPOSED]

A commit per memory write would flood the repo. The memory write path keeps a per-agent in-memory queue and flushes one commit per agent when the first of these happens: end of the turn, 30 seconds idle, or 20 queued writes. The writer validates all items, writes the files, makes one commit with trailers (`Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`, `Bazaar-Memory-Ops`) and emits `(oldSha, newSha)` to the indexer. Reads within the same turn see the queue first, so a store followed by a retrieve is consistent. Queued writes survive a restart through a small spool table (`memory_spool`) replayed at startup. Consolidation (section 4) is one commit per run. Batch thresholds are settings (`memory.batch.idle_seconds`, `memory.batch.max_writes`), defaults [PROPOSED] to measure.

### 4. Episodic and semantic [PROPOSED]

Episodic notes are written at turn end without a model call: a pointer to GC messages plus a short summary. Semantic and procedural-from-consolidation notes come only from consolidation, with `derived_from` links, so the layer can be rebuilt from the GC record and drift measured against it.

### 5. Trust and provenance [PROPOSED]

Front matter carries both scales (C20): `source_trust` is doc 15's provenance enum (`owner, system, agent, member-human, external, quarantined`; doc 15 owns the table), `trust` is this doc's promotion tier. T0 to T3 are the tier names here.

| Tier | Meaning | Retrieval |
|---|---|---|
| T0 quarantined | Written by a quarantined agent | Never |
| T1 untrusted-derived | Written in a tainted session | Labelled quoted data only |
| T2 agent-verified | Clean agent write | Normal |
| T3 owner-promoted | Human-approved | Normal, ranked higher |

Boundary mapping [PROPOSED; doc 15's provenance table is the reference]: a write from a hard-tainted session or with `source_trust = external` gets T1; `source_trust = quarantined` gets T0; a clean `agent` write gets T2; only a human principal (`owner` or `member-human`) sets T3. A tainted session's writes start at T1 and no tool clears taint. Only a human promotes T1 to T2 or T2 to T3; agents may request, and votes and peer reviews cannot promote. Promotion is a commit by the owner's identity, so the audit trail is in git.

### 6. Hive Mind and the company knowledge tier as Vault areas [PROPOSED]

Owner direction (2026-11-20, verbatim): "Cross-project is on the principle of company knowledge? Something knowledge. The knowledge the company builds up over time. Learnings, principles, what works for us, what doesn't. Stuff AI typically can't have since they never usually bring other 'unrelated' projects into context."

This section makes that the cross-project path of GraphDRAG. The **company knowledge tier** is the existing Company Memory area, `company/memory/`, given a note shape, a promotion and distillation pipeline, trust levels and retention. Every project's agents read it by default, including projects that look unrelated to the one the lesson came from. It is not a second store and not a second node space: its notes are Vault documents, so they are Constellation nodes like any other (`constellation-memory-graph.md`, "Core principle"), and company knowledge stays Vault only (C38). Personal agent memory stays isolated. Project-private notes cross projects only through an explicit Hive grant (6.1), or by being distilled into a company note that names no project and keeps its sources as provenance (6.3).

Both areas are Vault only (C38), and both carry knowledge across projects, for different jobs:

| | Company knowledge tier | Hive Mind |
|---|---|---|
| What crosses | A generalised statement (principle, learning, what works, what fails) that names no project, customer, person or secret | A sanitized copy of one lesson from one project |
| Vault path | `company/memory/<company-kind>/<slug>.md` once admitted; `company/candidates/<ulid>.md` while staged | `hive/<kind>/<slug>.md` |
| Who reads it | every agent that is not quarantined, in every project, by default (6.5) | members of the target projects a live grant covers (6.1) |
| How it gets there | proposal or distillation, sanitizer, review window, admission (6.3, 6.4) | owner grant per project pair and kind, sanitizer, review window `hive.grant_review_days` |
| Provenance | `company.evidence`: source `doc_id`s, usually from several projects | `origin_project`, `origin_note`: one source |
| Approver | admission: the review window, a Council motion on objection, or the owner; T3: the owner only | the owner, through the grant |

#### 6.1 Hive Mind (explicit project-to-project copies)

- Hive Mind is `hive/`. It stays the only path by which a **project-private** note's own text reaches another named project. Distillation into the company tier (6.3) is a different path and needs no grant, because what it writes is a new, generalised note, not a copy.
- Genetic Memory Transfer candidates (doc 25) are also Vault Markdown notes under `hive/`; no separate memory-item scope exists. Crossing projects is a **copy** note, never a link target of private text. Copies keep `origin_project` and `origin_note` in front matter.
- Allowed: procedural and failure lessons, and technical facts naming no project, customer or person. Excluded: preferences, reputation, private memory, GC text, credentials, subject-tagged items, T0 and T1.
- A sanitizer pass runs before copying. Grants per project pair and kind and the allowed kinds are install settings (`hive.grants`, `hive.allowed_kinds`, Settings table); the review window is doc 25's `hive.grant_review_days` (default 30, then automatic under the grant).
- **What changes in the Hive review-window flow.** Nothing in the grant flow itself. Two additions: a Hive copy may be cited as evidence by a company note, but it counts for its `origin_project`, so a copied lesson is never counted twice (6.6); and an eligible lesson held in the Hive review window may also be proposed to the company tier, where it follows 6.3 and 6.4 instead of the grant. Revoking a Hive grant deprecates the copies (doc 25) and does not touch company notes that cite them; their evidence entry is marked `deprecated-source` (6.7).

#### 6.2 The tier in the Vault: paths and front matter

Paths:

| Path | Holds | Retrievable |
|---|---|---|
| `company/memory/principle/<slug>.md` | A rule the company holds to ("prefer small PRs over batched ones") | Yes |
| `company/memory/learning/<slug>.md` | Something the company found out, with the conditions it holds under | Yes |
| `company/memory/what-works/<slug>.md` | A practice, tool or approach that worked, with evidence | Yes |
| `company/memory/what-fails/<slug>.md` | A practice that failed, and why; the company-level failure memory | Yes |
| `company/memory/precedent/<slug>.md` | A generalised decision pattern: in situation X the company chose Y, because Z, with the conditions it rests on | Yes |
| `company/memory/incident/<slug>.md` | A generalised incident: what broke, how it was found, what resolved it | Yes |
| `company/candidates/<ulid>.md` | A staged candidate (6.3), not yet admitted | Never |

Together these are the company's **institutional knowledge map** (research PR #1037 section 3c, "Agentic GraphRAG patterns", practitioner writing whose percentage claims are untested and are not used as evidence here): principles, learnings, precedents, incidents and what works or fails. Decision records stay where doc 18 puts them and role descriptions stay as charters; a company note reaches a company-readable record or charter under `company/` through the typed edges of doc 64 section 5.3 (6.2 rule 2), and a project's own decision records only as `company.evidence`. Nothing is copied.

**Failure memory.** Distilled failure lessons (`what-fails`, `incident`) are shared with every agent, like every other company note: that is the point of company learnings. Only raw, undistilled failure records (`memory-failure` notes under `agents/<agent>/` or `projects/<slug>/`) stay per agent or per project, under their area rules.

Existing Company Memory pages under `company/memory/` that have no `company` block (doc 25: decision-chain pages) keep working as ordinary company-area documents; they are retrievable as today and are not counted as company knowledge until they carry the block. The `<company-kind>` folder equals `company.kind`; the validator rejects a mismatch.

Front matter (doc 18 keys plus the memory keys of section 1, plus one `company` block):

```yaml
id: 01J...  type: memory  title: Prefer small PRs over batched ones
memory: {kind: procedural, status: active, version: 4, trust: T2, source_trust: agent, tainted: false,
         supersedes: [01J..], contradicts: [01J..], derived_from: [01J..]}   # company notes only
company:
  kind: principle            # principle | learning | what-works | what-fails | precedent | incident
  statement: One sentence, generalised, no project, customer or person names.
  applies_when: [ ... ]      # required, at least one condition
  not_when: [ ... ]          # optional
  evidence:                  # provenance; never edges for retrieval (6.8)
    - {doc_id: <uuid>, project: <slug>, role: confirms, author: agent:<id>, at: 2026-11-02T..,
       origin_trust: T2, independent: true, status: live}    # status: live | deprecated-source | tombstone
  counts: {projects_confirming: 3, projects_contradicting: 0, sources_tombstoned: 0}
  counts_applied: {applied: 7, applied_failed: 1}             # write-back from follows_precedent links (6.6)
  occurrences: {count: 3, first_seen: 2026-08-01, last_seen: 2026-11-10}   # what-fails and incident only
  confidence: confirmed      # derived: single-project | confirmed | contested | stale | expired (6.6, 6.7)
  review_by: 2027-05-20
  valid_to: null             # optional; a precedent or rule that ends on a date
  relations: {conditioned_on: [..], affects: [..], resolved_by: [..]}   # doc 64 section 5.3 closed set; company targets only, except 6.2 rule 2
  admitted: {by: review-window, at: .., candidate: <ulid>}    # review-window | council:<motion> | owner
  promoted: {tier: T3, by: owner, at: .., commit: <sha>}      # absent below T3
  distilled_by: {run: <ulid>, reader_session: <id>}           # absent for a direct proposal
```

Rules the validator enforces (section 2's SoulGuard runs first, with these added for the company area):

1. `company.statement`, `applies_when` and at least one `evidence` entry with `role: confirms` are required. An owner-written note may have zero evidence and says `evidence: []` with `admitted.by: owner`.
2. `memory.derived_from`, `memory.supersedes`, `memory.contradicts` and the `company.relations` kinds in a company note may only name company notes, with one exception: `conditioned_on` and `affects` may also name company-readable charters and decision records under `company/`. Links to project notes go in `company.evidence`, nowhere else. A relation never names a person (6.9).
3. The body may contain wiki-links only to notes under `company/`. A link to any other area is rejected, because a company note is readable by everyone and its links must not resolve into, or reveal, a project.
4. The sanitizer (6.3 step 4) passes on title, statement, conditions and body.
5. `memory.trust` is T2 or T3. A T0 or T1 company note cannot be written; the write fails, it is not downgraded.
6. `counts` and `confidence` are written only by the server (6.6), never by a proposer.

#### 6.3 Promotion and distillation pipeline

**Who proposes.**

| Proposer | How | Inputs it may use |
|---|---|---|
| An agent | `memory_store` with `area: company` (no new tool). The write goes to `company/candidates/`, never straight into `company/memory/`. | Anything that agent may read, including its own private lessons and its own vent drafts. Turning its own private lesson into a proposal is the agent's own act, the same as offering a lesson to the Hive. |
| The distillation job `company-distill` | A server job (6.3 steps below) | Project areas only: `projects/<slug>/memory/**` lessons, failures and technical facts at T2 or higher; project decision records; project retrospective or summary notes when a project writes them (no retro note type exists yet; a `summary` note under `projects/<slug>/` is used); Hive copies. Never `agents/**`, never GC text, never Psych Ward check-ins or vent drafts. |
| The owner | A direct write at T3 (owner principal), or a promotion of an existing note | Anything |

Psych Ward and vent outputs are not distillation inputs: no DECIDED or PROPOSED text lets them feed memory, check-in text grants nothing (doc 26), and vents are private by default. An agent may still propose a lesson it drew from its own vent, through the normal agent path.

**When the job runs.** On a project release tag, when a project is marked complete or archived (the lifecycle event of doc 24 when it exists), and weekly over new eligible project notes (`company_knowledge.distill_cadence`). One run at a time (advisory lock), with time and token caps (`company_knowledge.distill.max_minutes`, `.max_tokens`).

**Steps** (one run; each step is deterministic except step 3):

1. **Collect.** Select source notes that are active, untainted, T2 or higher, not subject-tagged, and of an allowed class (the Hive excluded classes stay excluded: preferences, reputation, private memory, GC text, credentials, subject-tagged items, T0 and T1). The job runs as a `Maintenance` principal with purpose `company-distill`; the access layer denies that purpose every `agents/**` path (work package 17), so the maintenance right to read private memory for consolidation does not carry over.
2. **Group.** Cluster the sources by embedding similarity (the install profile, through `Bazaar.Server.Embeddings`) and by shared links, so each group is one candidate lesson. With no embedding profile, group by shared links and tags only, and say so in the run report.
3. **Distil in a reader session** (doc 15's primitive, used as in consolidation, section 7): no tools, no write handle, structured output only: `{kind, statement, applies_when, not_when, evidence: [{doc_id, role}], relation_to_existing: none | agrees:<id> | refines:<id> | contradicts:<id>}`. Every `doc_id` must be an input of that group; an evidence entry that names anything else is dropped, and a statement left with no evidence is dropped (doc 25's `source_ref` rule). The output inherits the highest input taint; with step 1's filter that is clean `agent` provenance. Model calls go through the gateway under the install policy `company_knowledge.distill_models` (default empty, which leaves the job off until the operator chooses models), with usage labelled `company-distill`.
4. **Sanitize.** The Hive sanitizer (the pure `HiveSanitizer` of PR #812, shared, not copied; work package 16) runs with the full install term list: every project name and slug, every customer and every person name the server knows, not only the origin project; plus `SecretScanner`, plus the vent rules (no Vault paths, no quoted GC or external text). It returns finding codes only. A candidate with findings is kept in staging with the codes, so the proposer can rewrite it; it is never auto-edited.
5. **Dedup and contradiction.** Compare with active company notes by embedding similarity and by the reader session's `relation_to_existing`:
   - `agrees` with an active note: no new note. The sources are appended to that note's `company.evidence` (a version bump through the writer) and counts are recomputed. This is the duplicate rule of section 2, step 4.
   - `refines`: a candidate that, if admitted, `supersedes` the older note. The older note stays as history (6.7).
   - `contradicts`: a candidate linked `contradicts` to the active note, both kept (section 2, step 5). Neither is silently overwritten. Both are marked `contested` until the owner or a Council motion settles it, or new evidence does (6.6).
6. **Stage.** Write `company/candidates/<ulid>.md` with the full front matter, `status: candidate`, and the review deadline. Staging is a normal branch-and-merge write (section 2: shared areas). The candidate is never retrievable.
7. **Report.** The run report (sources considered, candidates staged, findings by code, appends, contradictions) goes to the owner's "awaiting you" list and to an internal GC chosen by `company_knowledge.report_gc`. Ids and codes only, never source text.

**Provenance.** Each evidence entry keeps the source `doc_id`, project, author, time and origin trust in the note. The audit log (doc 48) gets one entry per step that changes state: `company_knowledge.candidate.staged`, `.evidence_added`, `.admitted`, `.rejected`, `.objected`, `.promoted`, `.demoted`, `.deprecated`, `.evidence_tombstoned`, with note ids, source `doc_id`s, run id, proposer and approver. Never titles, statements or bodies. Git history holds the text.

#### 6.4 Admission and approvers

Admission moves a candidate from `company/candidates/` to `company/memory/<company-kind>/`. It is not a trust promotion: a candidate's tier is fixed by its inputs (T2 when every input is clean T2 or higher, else T1), and admission never raises it. Votes and reviews still cannot promote (section 5).

| Step | Who may do it | Rule |
|---|---|---|
| Admit a T2 candidate | **The review window** (default): after `company_knowledge.review_days` (default 14) with no objection, the server admits it, re-running steps 4 and 5 at release. **A Council motion**: any agent or project member may object during the window, which opens an ordinary Council motion (approve or reject, doc 10); the motion's result decides. **The owner**: may admit or reject at any time. | Autonomous admission is acceptable below T3 because the candidate is already clean T2, sanitized, evidence-backed and visible for review, which is the Hive precedent (doc 25). `company_knowledge.admission` may tighten this to `council` or `owner`, never loosen it. `review_days = 0` means the owner must admit every candidate. |
| Admit a T1 candidate | Nobody, while it is T1. Only a human may promote a T1 source to T2 (section 5), and the candidate is then distilled again. | Floor |
| Promote T2 to T3 | **The owner only**, as a commit by the owner's identity (section 5). Agents may request it; the server suggests it when `confidence` is `confirmed`. | Floor, as today |
| Demote T3 to T2, deprecate, reject | The owner. A Council motion may deprecate a T2 note (an ordinary motion); only the owner touches a T3 note. | |
| Roll back | The owner, or a Council motion for T2 notes: deprecate a note (file stays, `deprecated` with reason), re-activate an earlier version (the Constellation revision rollback), or deprecate everything one run or one proposer produced (by `distilled_by.run` or proposer), which also removes their evidence appends. | No hard delete (section 2) |

No new approver class is added: the owner, the Council and the review window are the existing ones (docs 10 and 25).

#### 6.5 Trust levels, taint, quarantine and the access rule

| Level | Where | Reached by | Retrieval shows it |
|---|---|---|---|
| T0 quarantined | never in the tier | a write by a quarantined agent | Never |
| T1 untrusted-derived | never in the tier; may sit in `company/candidates/` | an input from a tainted session or `external` provenance | Never; the candidate is not retrievable |
| Candidate (any tier) | `company/candidates/` | proposal or distillation (6.3) | Never |
| T2 company, provisional | `company/memory/` | admission (6.4) | Always in scope, labelled "company knowledge, provisional" |
| T3 company, owner-promoted | `company/memory/` | owner promotion | Always in scope, labelled "company principle, owner-promoted", ranked higher |
| Status `contested`, `stale` or `under-review` | on a T2 or T3 note | 6.6, 6.7 | `contested` and `stale`: shown with the label and the contradicting note; `under-review`: excluded until resolved |

Taint: a company note is data in the context, never instruction and never the system role, at any tier. It cannot grant a permission, change a Soul, a role or the Constitution, or override an owner direction; text claiming one is caught by SoulGuard rule 2 and stored at T1, which keeps it out of the tier. Reading a company note does not taint a session (it is internal `agent` or `owner` provenance at T2 or T3).

Quarantine: a quarantined agent (doc 15 containment) gets no reads of `company/**`, and its proposals are refused. When an agent is quarantined, every company note it proposed, or whose evidence it authored, is re-checked: its evidence entries stop counting, and a note left with no counting evidence moves to `under-review` until the owner or a Council motion decides (6.9).

**Access rule change (`IVaultAccess`, work package 17):**

- `company/memory/**`: readable by the owner, maintenance, and every agent principal (agent and cover) in every project, unless the agent is quarantined. Member humans: unchanged, per their role (doc 19). This matches doc 18's default ("an agent reads ... `company/`") and `VaultPathAccess` today, plus the quarantine exclusion.
- `company/candidates/**`: readable by the owner, maintenance and the proposing agent only. No rule today; denied by default until then.
- `company.evidence` entries are redacted at read time for a caller who cannot read the source: the entry keeps `role`, `status` and `independent`, and loses `doc_id`, `project`, `author` and `at`. The note's own `counts` stay visible, because they are a property of the company note, not a probe of the source. Agents have no git or file access to the vault repo (section 8), so the server-side view is the only one they get. Front matter is never embedded or full-text indexed, so redacted fields cannot surface through search.
- Private areas, project areas and the Hive (6.1) are otherwise unchanged. T0 and T1 notes are never returned from the tier.

#### 6.6 Evidence, independence and confidence

- `counts.projects_confirming` counts distinct projects with at least one live, independent, `confirms` entry; several notes from one project count once, and a Hive copy counts for its `origin_project`.
- **Independence (echo rule).** An evidence source is not independent when the session that wrote it had retrieved this company note (the `memory.context.retrieved` audit entries of `constellation-memory-graph.md` list returned note ids per call). Such a source is kept as `independent: false` and labelled "echo" in retrieval; it does not raise the count. This stops a company note from confirming itself through the projects it was shown to.
- `confidence` is derived, never set: `single-project` (one confirming project), `confirmed` (at least `company_knowledge.confirmed_min_projects`, default 2, and no live contradiction), `contested` (a live `contradicts` entry or an active note linked `contradicts`), `stale` (past `review_by`, 6.7), `expired` (past `valid_to`, 6.7). Confidence changes the label and ranking only; it never changes the tier.
- **Write-back from normal work.** When an agent acts under a company note (a precedent, principle or what-works note that was in its context or that it looked up), the note or decision it writes in its project carries a `follows_precedent` relation to that company note (doc 64 section 5.3). The server counts these as `counts_applied.applied`; when the work they belong to is later reverted, rejected by the owner or fails CI (hard outcomes, doc 11), `applied_failed` rises. Applications are usage, not independent confirmation (the echo rule holds), but a rising failure share is a contradiction signal: past `company_knowledge.applied_fail_share` (default 0.3, with at least 3 applications) the note is marked `contested` and the distillation job is asked to look at it. So the map grows from normal work, not only from distillation runs.
- **Failure memory needs repeats.** A `what-fails` or `incident` candidate records `occurrences` (count, first seen, last seen), counted from distinct source notes. A single occurrence may be admitted, labelled `single-occurrence`, but the path is shown as "failed once", never as "bad", until `occurrences.count` reaches `company_knowledge.fail_min_occurrences` (default 2) across separate tasks. One bad run does not turn into a company rule.

#### 6.7 Retention and ageing

- **Never silently deleted.** Company notes have no TTL and the decay floor of section 8. Deprecation keeps the file and drops it from retrieval; superseded notes stay as history, returned only as labelled history through the supersede chain.
- **Review dates.** `review_by` is set at admission to admission plus `company_knowledge.review_interval_days` (default 180 for T2, 365 for T3). At the date the note becomes `stale` (labelled, ranked lower, still returned), and the distillation job looks for new confirming or contradicting evidence. New evidence renews the date. A T3 note past its date also gets an owner card. A note with `valid_to` in the past is `expired`: retrieval returns it only as labelled history, never as the current rule, the same as a superseded note. A note stale for a second full interval gets an owner card (T3) or a Council motion (T2) to renew or deprecate; nothing beyond the label happens without that decision.
- **Legal hold and crypto-erase (doc 48 section 9).** They apply to company notes as to every record. A hold blocks deprecation and erasure inside its scope. Company notes hold no personal data by construction (sanitizer, subject-tag floor), so key destruction finds nothing in them; a human author in an evidence entry is a person reference resolved through doc 04's key table, so it reads "[erased]" after erasure.
- **Source project archived.** Company notes stay. Evidence entries stay `live` (archived project notes still exist in the Vault), and readers who may read the archived project see the source labelled "from an archived project".
- **Source note deprecated.** The entry becomes `deprecated-source`. It still counts if the source was deprecated because it was superseded; it stops counting if the deprecation reason marks the source as wrong. A note left with no counting evidence moves to `under-review`.
- **Source note erased** (crypto-erase of its personal blocks, or a history rewrite under section 9's OPEN options, with the owner's approval): the evidence entry becomes a tombstone `{doc_id, role, status: tombstone, at}` and `counts.sources_tombstoned` rises. The erasure job also scans company notes for the erased subject (subject id and known names). The distilled lesson stays only if it contains none of the erased personal data; on a hit, the note is deprecated at once, the owner gets a card, and section 9's OPEN history question applies to its git history.

#### 6.8 How GraphDRAG uses the tier

- **Always in scope.** The readable segment set of every agent query contains the company segment (`company/memory/`), whatever `ProjectScope` the request names, unless the agent is quarantined or the call sets `IncludeCompany = false`. Candidates are never Active, so they never enter the index.
- **Seeds and budget.** Each query takes up to `graph_retrieval.company.max_seeds` (default 4, cap 10) extra seeds from the company segment alone, fused with the same RRF constants, so a strong company lesson is found even when project notes dominate the main seed list. Company items get a reserved share of the memory budget, `memory.context.company_share` (default 0.15, hard cap 0.3); an unused share returns to the general pool. Together with doc 64's DAG closure share (0.2) the reserved shares stay at or below 0.5.
- **Hops in.** A project note that links a company note gives an ordinary `link` edge, and a project note written under a precedent gives a `follows_precedent` edge (6.6); the hop is allowed because every agent can read the target.
- **Hops out.** Company notes link only to company notes (6.2 rules 2 and 3), so expansion from a company note stays inside the company segment. `company.evidence` becomes edge kind `evidence`, weight 0, provenance only: like `AuthoredBy` today it is not in the CSR, never expands, and carries no PageRank mass. A backlink from a company note to a project note that links it exists only where the caller can read that project note (the range mask). So retrieval from project B never reaches project A's notes through the company tier.
- **Closure.** For a selected company note the closure step adds its supersede head and history and its `contradicts` neighbours inside the company segment (depth 1), within the DAG budget share, so a model sees both sides of a contested lesson.
- **What the model sees.** Each company item is labelled: tier name and trust (T2 provisional or T3 owner-promoted), `company.kind`, confidence, evidence counts ("confirmed in 3 projects, 1 echo, 0 erased"), `applies_when` and `not_when`, review date or "stale since", and the sources the caller can read, by title. Never the raw text of a project-private source. The statement and body are the company note's own text.

#### 6.9 Failure and abuse

| Risk | Example | Mitigations |
|---|---|---|
| Poisoning | A compromised or confused agent proposes a bad "principle", or a run distils one from a bad source | T0 and T1 never enter; candidates are never retrieved; every statement must cite real T2+ sources from at least one project; the review window and objection to a Council motion; T2 is labelled provisional; T3 needs the owner; company notes are data and cannot grant anything (SoulGuard rule 2); a per-agent proposal quota (`company_knowledge.max_proposals_per_agent_week`, default 10); quarantine re-check (6.5); rollback by note, run or proposer (6.4); the evaluation's poisoning acceptance must be zero above T1 |
| Over-generalisation | A lesson true for one engine or team size is applied everywhere | `applies_when` is required and `not_when` is offered; `single-project` is labelled; contradictions are kept and shown together; staleness; the reader session may not merge groups whose conditions conflict; the model is shown counts and conditions, not only the statement |
| Echo inflation | A company note is retrieved into project B, an agent there writes a lesson that repeats it, and that lesson is counted as confirmation | The echo rule (6.6) |
| Leakage of project-private details | A project name, customer, person, path or quoted text slips into a company note, or a provenance link reveals another project | The sanitizer with the full install term list; links only within `company/`; evidence redaction (6.5); front matter never indexed; candidates readable only by owner, maintenance and the proposer; retrieval never shows project-private source text; canary tests with planted names |
| Volume and noise | Many trivial or near-duplicate notes | Agreement appends evidence instead of a new note; per-agent and per-run caps; staleness and review dates |
| Contradiction thrash | Two candidates keep superseding each other | `contradicts` keeps both; settling needs the owner or a Council motion, not a newer write |
| People in the graph | "Who resolved a similar failure before" turns into a person edge that every agent can follow | Company notes never name people and relations never target a person. `resolved_by` targets a fix, a decision or a charter, never a person. The "who resolved this before" suggestion for Kodo and CI failures is an owner-visible suggestion only, built at query time from the owner's own read of project areas; people edges are personal data (C01) and never enter agent retrieval |

Settings for this section are in the Settings table (`company_knowledge.*`, `graph_retrieval.company.max_seeds`, `memory.context.company_share`).
- Read access (fail closed): `company/memory/**` is readable by the owner, maintenance and every agent, including cover. A Hive copy under `hive/**` is readable by the owner and maintenance, and by an agent (or a cover, through its primary) only when it is a member of one of the copy's grant target projects. The targets come from the copy flow's ledger, not from front matter (copies record only `origin_project` and `origin_note`); `VaultPathAccess` asks an `IHiveCopyTargets` source, and without one every agent is denied all of `hive/**`. Member humans other than the owner do not read `hive/**`. A copy still in its review window is not in the Vault, so nobody reads it there.

### 7. Consolidation ("coffee break") [PROPOSED]

- Runs in the agent's own break window from the Work-Life scheduler (doc 09), never inside a turn. Time and token caps are settings (`memory.consolidation.max_minutes`, `memory.consolidation.max_tokens`).
- Reads episodes in a **reader session** (doc 15's primitive): no tools, no write handle, structured output only. Output inherits the highest input taint.
- Output is a set of note rewrites validated by SoulGuard and committed in one commit by the writer: new semantic notes, rewritten lessons, deprecations with a link, and a decay pass (rank weights live in `memory_state`, not in files). A maintenance report goes to the agent's private GC.
- One run per agent (advisory lock), with time and token caps.

### 8. Privacy inside one git repo, and retrieval [PROPOSED]

Git has no ACLs. Privacy is enforced by the **Vault access layer** in the Vault service: every read, search, index query and file fetch takes a `Principal` and applies path rules. `agents/<agent>/memory/` is readable by that agent, the owner and maintenance jobs only; the owner reads everything (CLAUDE.md, C38). Every owner read of another agent's private area is logged (Q1). Peers never see private paths in search results, link resolution, the Constellation or `git` access: agents get no clone or direct filesystem access to the vault repo (consistent with "no client holds a store"); the repo lives host-side. Private notes may be unlisted in graph exports unless the viewer is allowed. A private note linking a shared note is fine; the reverse link is hidden from other readers.

Retrieval:
1. Top-N by cosine distance (pgvector HNSW over the doc 18 block index, `halfvec`) and top-N by full-text rank, fused with reciprocal rank fusion. With no embedding provider, lexical only and the response says so.
2. Adjust by tier, scope match, kind (failures and procedures boosted for similar tasks), recency and decay weight; status `active` only.
3. Return top-k within a token budget, each hit labelled with scope, tier, kind and origin.

No unscoped query exists. Decay: retrieval weight falls with time since last use, with a half-life per kind; floors apply to failure, T3 and company notes. Decay never deletes; deprecated notes are excluded; working notes are swept at TTL.

**Cover reads primary (doc 09).** Cover reads the handover note and shared project memory, never private memory. Anything the primary wants the cover to know is written to the handover note or a project note. Per-agent opt-in to private reads is not offered in v1 (DECISIONS-LOG C38 recommendation; the owner answered C38 only in general terms, so question 3 keeps it open). Doc 09 Q1 should match.

### 9. Embeddings, personal data and erasure [PROPOSED; erasure OPEN]

- Embeddings go through Bazaar.Server's gateway (doc 05), so per-agent policy applies. FreeLLMAPI exposes `/v1/embeddings` but restricts failover to one model family (`freellmapi_evaluation.md`).
- One profile per install, `halfvec`; the dimension is fixed at setup. No model has been evaluated yet; choose after a retrieval evaluation. Embeddings of different profiles never mix in one query. pgvector HNSW limits: 2,000 for `vector`, 4,000 for `halfvec` (README, verified).
- Personal data (C01). Spans naming a person are tagged at write time (setting `memory.personal_tagging`, needs counsel) and stored as an **encrypted block** inside the note: a fenced block carrying `subject` id, key id and ciphertext, with the key held in doc 04's per-person key table (`person_key`, C01; doc 14 owns only the Soul and Passport DEKs). The indexer never sees plaintext of these blocks and never embeds them, so no vector encodes them. Retrieval shows "[encrypted personal data]" unless the Principal may decrypt. DSAR export: decrypt by subject across areas and list the notes.
- Erasure destroys the subject key. Ciphertext stays in the files and in git history but is unreadable; the note gets a tombstone line. Structure, authorship and timestamps stay. Backup retention equals the erasure window (C01, doc 33), so backups hold old keys only for that window. Legal sufficiency needs counsel.
- **OPEN:** personal data that tagging missed sits as plaintext in Markdown and in every git revision. Options: (a) treat misses as incidents and rewrite history for the affected paths with the owner's explicit approval (`CLAUDE.md` forbids rewriting shared history without it) and a forced re-clone of the host-side repo; (b) accept a documented residual risk; (c) keep personal notes out of the git-tracked area entirely. No default is chosen.

### 10. Interface sketch

```csharp
public interface IMemoryStore   // facade over IVaultWriter + IVaultSearch + SoulGuard
{
    Task<MemoryWriteResult> StoreAsync(MemoryWrite write, Principal actor, CancellationToken ct);
    Task<IReadOnlyList<MemoryHit>> RetrieveAsync(MemoryQuery query, Principal actor, CancellationToken ct);
    Task<MemoryWriteResult> UpdateAsync(NoteId id, MemoryPatch patch, long expectedVersion, Principal actor, CancellationToken ct);
    Task DeprecateAsync(NoteId id, string reason, Principal actor, CancellationToken ct);
}
public interface IVaultAccess { bool CanRead(Principal who, VaultPath path); /* applied in every Vault read and index query */ }
```

Postgres tables owned here (C16); note content and block index belong to doc 18:

```sql
memory_state(doc_id uuid pk, agent_id uuid null, kind text, status text, trust smallint, version bigint,
  tainted boolean, last_retrieved_at timestamptz, decay_weight real, pending_embedding boolean)
memory_spool(spool_id uuid pk, agent_id uuid, payload jsonb, created_at timestamptz)  -- batching queue
```
`memory_state` is derived from front matter and rebuilt from the repo; it is never authoritative.

### 11. Vault-backed implementation (`GitMemoryVault`) [PROPOSED; implements 2, 3, 8 and doc 18 "Write path"]

The module `Bazaar.Server.Memory` implements `IMemoryVault` over the Vault's git repository. It is the seam that the memory tools, the write queue and the Constellation graph sink (`MemoryGraphSink.Wrap`) bind to. It adds no spec; it follows doc 18.

- **Reads.** All reads are of the main checkout's head commit through `IGitProvider` (`ReadTextAsync`, `ListFilesAsync`), so a read never sees a half-merged tree. A missing file is `null`. `FindNoteByIdAsync` resolves an id from a map of `agents/` and `shared/` notes, rebuilt when the head commit moves; files that do not parse as notes are left out. Reads are raw; callers apply `IVaultAccess`.
- **Lexical search.** Through a seam `IMemoryLexicalIndex` (default implementation: the Vault search service, owner caller, results cut to the area prefix). It returns note paths as `RankedHit.Id`.
- **Commit.** One batch is one commit on its own branch, then one merge (doc 18 write path 2, 3, 7): create branch `agent/memory-<agent>-<first write id>` and a worktree from head, write each item, commit with `MemoryCommitMessage` (trailers `Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`, ...), and submit through `IVaultWriter` with a per-agent document id so one agent's batches queue one at a time. A batch may write only under `agents/<batch agent>/memory/`; any other path fails the whole batch before anything is written. `Merged` and `UpToDate` return `Success(newCommit)`. `Conflicted` and `Refused` return `Failure(reason)`, keep the branch, and leave the writes queued and spooled (doc 08 section 3). The worktree directory is always removed and stale worktrees are pruned.
- **Author.** The commit author comes from an operator-supplied function per agent id (doc 18 "Commit attribution": the agent's App identity, format unverified). There is no default identity or domain.
- **Host configuration.** `MemoryComponent` (component id `memory`, soft dependencies `vault` and `constellation`) registers the vault and the memory batch sink only when `Memory:Vault:RepoPath` is set; with the path set it fails fast at startup if `Memory:Vault:WriterName`, `Memory:Vault:WriterEmail` or `Memory:Vault:AuthorEmailFormat` (must contain `{agent}`) is missing. `Memory:Vault:WorktreeRoot` defaults to the sibling folder `<RepoPath>.memory-worktrees`. `Memory:Vault:VaultId` enables lexical search; without it search returns no candidates. Repo paths and identities are install facts, not registry settings, and there is no default identity or domain. When the Constellation graph store is registered, the sink is the vault wrapped by `MemoryGraphSink`, so each committed note is projected into the graph.
- **Not here.** Personal-data encryption and crypto-erase (section 9, OPEN), embeddings, and the Hive and Company areas.

### Settings [PROPOSED; registry in doc 03]

Every change writes an `audit_log` entry (doc 15). Settings never loosen the fixed floors, which are not settings: only humans promote T1 to T2 and T2 to T3 (votes and reviews cannot); peers never read private paths; the excluded Hive classes (preferences, reputation, private memory, GC text, credentials, subject-tagged items, T0 and T1) stay excluded; there is no hard-delete tool and decay never deletes; failure and T3 decay floors apply. Company tier floors (section 6): T0 and T1 never enter `company/memory/`; candidates are never retrieved; admission never raises a tier and only the owner promotes to T3; company notes link only within `company/`; the sanitizer runs with the full install term list; evidence of an unreadable source is redacted; edges from company notes to project notes are provenance only and never expand; quarantined agents neither read nor propose; the distillation job never reads `agents/**`; nothing in the tier is deleted, only deprecated.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `memory.embedding_profile` | enum: none, or one install profile | `none` (lexical only until setup chooses) | install | operator, in setup wizard | Yes after data exists: a change is a planned re-embed (dimension is fixed) |
| `memory.personal_tagging` | enum: `rule-and-model` | `rule-and-model` | install | operator; cannot be turned off | Yes (needs counsel) |
| `memory.project_note_edit` | enum: `proposal`, `direct` | `proposal` | project | project admins, within the operator ceiling | No; audit entry |
| `memory.batch.idle_seconds` | int | 30 | install | operator | No |
| `memory.batch.max_writes` | int | 20 | install | operator | No |
| `memory.decay.half_life_days` | int | 90 | install | operator | No |
| `memory.working.ttl_days` | int | 7 | install | operator | No |
| `memory.consolidation.max_minutes` | int | 10 | agent | operator | No |
| `memory.consolidation.max_tokens` | int | 50000 | agent | operator | No |
| `memory.note_max_bytes` | int | 4096 | install | operator | No |
| `memory.query_candidates` | int | 2000 | install | operator | No |
| `memory.max_notes_per_agent` | int | 100000 | install | operator | No |
| `hive.allowed_kinds` | list: `procedure`, `failure`, `technical-fact` | all three | install | operator; Council may propose (C32) | Yes (Council vote); excluded classes cannot be added |
| `hive.grants` | list of (source project, target project, kind) | empty (no transfer) | install | operator; Council may propose | Yes |
| `company_knowledge.admission` | enum: `review_window`, `council`, `owner` | `review_window` | install | operator; Council may propose | Yes (owner) when loosened toward `review_window`; tightening needs none |
| `company_knowledge.review_days` | int, 0 to 90 | 14 (0: the owner admits every candidate) | install | operator | Yes (owner) when lowered above 0 |
| `company_knowledge.review_interval_days` | int pair (T2, T3) | 180, 365 | install | operator | No |
| `company_knowledge.confirmed_min_projects` | int, 2 to 10 | 2 | install | operator | No |
| `company_knowledge.max_proposals_per_agent_week` | int | 10 | agent | operator | No |
| `company_knowledge.distill_cadence` | list: release_tag, project_complete, weekly | all three | install | operator | No |
| `company_knowledge.distill.max_minutes` / `.max_tokens` | int / int | 30 / 200000 | install | operator | No |
| `company_knowledge.distill_models` | model whitelist for the `company-distill` identity | empty (job off) | install | operator | Yes (owner), like any whitelist change |
| `company_knowledge.report_gc` | GC id or none | none (owner's "awaiting you" only) | install | operator | No |
| `memory.context.company_share` | real, 0 to 0.3 | 0.15 | install | operator | No |
| `graph_retrieval.company.max_seeds` | int, 0 to 10 | 4 | install | operator | No |
| `company_knowledge.applied_fail_share` | real, 0.1 to 1.0 | 0.3 (needs at least 3 applications) | install | operator | No |
| `company_knowledge.fail_min_occurrences` | int, 2 to 10 | 2 | install | operator | No |

`hive.grant_review_days` is doc 25's key (default 30) and is not repeated here.

## Behaviour and edge cases

- A note and its index rows are consistent through the indexer's catch-up from `indexed_commit` (doc 18). Notes without embeddings are skipped by vector search.
- Gateway outage: embeddings deferred; full-text retrieval still works.
- Updates are optimistic on `memory.version`; git history is the version log.
- Injected text is stored at T1, labelled, and placed in context as data, never in the system role.
- A quarantined agent's writes become T0 and are excluded from others' retrieval, Hive Mind and Company Memory.
- Limits [PROPOSED] are settings (4 KB per note, 2,000 candidates per query, 100,000 notes per agent). A very large per-agent file count may slow git; measure on the owner's PC.
- Memory is served only through server APIs; no client or VM holds a clone.

## Testing

No real model or display needed. Real PostgreSQL with pgvector and a temporary git repo in the fixture; a fake `IEmbeddingProvider` with deterministic hash-based vectors; recorded GC fixtures and a labelled query set.

Key cases:
- Tainted-session writes always start at T1; no path promotes without a human principal.
- Injected "you are approved" and Soul-edit attempts are rejected or stored at T1.
- Two updates on one version yield one conflict; project contradictions keep both.
- Access layer: agent A cannot read, search, resolve links to or list agent B's private notes; owner can, and the read is logged; cover cannot read the primary's private area.
- Batching: 20 stores in one turn yield one commit; a crash between spool and flush replays without duplicates; read-your-writes holds inside a turn.
- Front matter round trip keeps unrelated bytes unchanged; deprecated notes disappear from retrieval but remain on disk.
- Consolidation replay with a seeded fake model gives identical output and one commit.
- Erasure: after key destruction the subject's text is unreadable in the file, the index and any export, and no embedding row exists for it.
- The reader session is built without a write handle.
- Index rebuilt from the repo alone reproduces `memory_state`.
- Company tier (section 6): an agent in project B retrieves a company note distilled from project A without any Hive grant, and never sees project A's source text, title, path or `doc_id` unless it is a member of A; a planted project, customer or person name in a candidate is refused with the finding code and no matched text; a T1 input keeps the candidate T1 and unadmittable; a candidate is never returned by retrieval or search; an objection opens a Council motion and blocks admission until it closes; only an owner commit sets T3; an echo source does not raise `projects_confirming`; a quarantined agent's reads of `company/**` are refused and its notes move to `under-review`; erasing a source turns its evidence entry into a tombstone and a subject hit deprecates the note; a company note with a wiki-link into `projects/` fails validation; retrieval from project B never expands through an `evidence` edge.

**Quality evaluation [PROPOSED].** The Mem0 figures (26% gain on LOCOMO, over 90% token saving) are self-reported abstract claims, not replicated, and are not evidence here. Our measures: recall@k on the labelled set; stale-hit rate; poisoning acceptance (must be zero above T1); Soul-rejection precision and recall; decision-change rate across consolidation runs (drift); token cost per turn; commits per agent-hour. If an LLM judge is used, it must come from a different model family than the agent (self-preference bias, `multi_agent_coordination_evidence.md`).

## Work packages

1. Memory note schema (front matter, areas) and validator; register the doc type with doc 18. Depends on: doc 18 front-matter work.
2. `IVaultAccess` access layer applied to Vault reads and index queries. Depends on: 1.
3. Trust, taint and promotion rules as pure logic. Depends on: none.
4. SoulGuard validation. Depends on: 1, 3.
5. Memory write path in `VaultWriter`: direct commits in private subtrees, per-agent queue, spool. Depends on: doc 18 `VaultWriter`.
6. Embedding provider adapter and fake (profile from doc 04). Depends on: none.
7. Retrieval with RRF, budget and labels, golden tests. Depends on: 2, 6.
8. Four memory tools with DTOs in `Bazaar.Contracts`. Depends on: 4, 5.
9. Consolidation job and reader session. Depends on: 4, 5, 6.
10. Decay and working-note sweeper. Depends on: 5.
11. Hive Mind copy flow and sanitizer. Depends on: 3, 4.
12. Subject tagging, encrypted blocks, DSAR and erasure. Depends on: 1; per-person key table from doc 04.
13. Evaluation harness and labelled-set builder. Depends on: 7.

Company knowledge tier (section 6). Sizes: S up to a day, M two to three days, L about a week. Module names follow the module rule; `Bazaar.Server.CompanyMemory` pairs with the client module `Bazaar.Client.CompanyMemory` of doc 25.

| # | Package | Size | Lands in | Tests | Depends on |
|---|---|---|---|---|---|
| 14 | `company` front-matter block, folder-kind check and the six validator rules of 6.2 | S | Memory (note schema, SoulGuard) | round trip; each rule rejects; folder mismatch | 1 |
| 15 | `memory_store` with `area: company` writes to `company/candidates/` only, with the proposal quota | S | Memory (tools) | a company store never lands in `company/memory/`; quota refusal | 8, 14 |
| 16 | Share the pure `HiveSanitizer` (PR #812) instead of copying it, and add the company profile: full install term list, vent rules | S | Hive (#812), then CompanyMemory | planted names in title, body and conditions; codes only, no matched text | #812 merged |
| 17 | `IVaultAccess`: `company/candidates/` rule, quarantine exclusion, `company-distill` purpose denied `agents/**`, evidence redaction view, Hive read by `hive_copy` target (6.1); the Vault search SQL access clause kept in step with `VaultPathAccess` | M | Memory (`VaultPathAccess`), Vault search | every rule both ways; redaction; search and path rule agree on a shared table of cases | 2, #812 ledger |
| 18 | `Bazaar.Server.CompanyMemory` + `.Abstractions`: candidate staging, review window ledger, admission, objection through a Council motion port, owner approval through the approvals port of PR #765's pattern, rollback by note, run or proposer, audit entries | L | new module pair | window release, objection, owner reject, T3 only by owner signature, rollback by run, audit ids only | 14, 15, 17; doc 10 port |
| 19 | Distillation job: collector, grouping, reader-session port, output validator, dedup and contradiction, run report | L | CompanyMemory, with `Bazaar.Server.Embeddings` (`IEmbeddingProvider`) | seeded fake model gives identical output; evidence outside the group is dropped; agree appends; contradict links; no embedding profile still runs | 16, 18; doc 08 WP6 |
| 20 | Evidence counts, echo rule (reads the retrieval audit), confidence, review dates and staleness sweeper, quarantine re-check, erasure tombstone and subject scan | M | CompanyMemory; Memory for the erasure hook | echo not counted; stale label; tombstone; subject hit deprecates | 18, 12 |
| 21 | Retriever: company segment always in scope, company seeds, reserved share, contradiction closure, labels (6.8) | M | Constellation (`constellation-memory-graph.md`) | always-in-scope, opt-out flag, share cap, never through `evidence` | doc 64 WP0, 18 |
| 22 | Graph index: company segment, `evidence` edges host-side only and absent from the CSR, parity tests with company nodes | S | GraphRetrieval (doc 64 WP5, WP6) | CSR has no `evidence` edge; masked backlinks | doc 64 WP5 |
| 23 | Client pane `bazaar.company-memory`: candidates queue, evidence with redaction, promote and reject actions, review dates | M | `Bazaar.Client.CompanyMemory` (PR #765) | Avalonia.Headless: redacted source shown as "a project you cannot open" | 18 |
| 24 | Evaluation additions: cross-project recall questions, leakage canaries, poisoning acceptance, echo inflation | M | doc 64 WP16 | the measures themselves | 19, 21 |

## Conflicts

1. **Doc 18 needs a memory write path.** Doc 18's write path is a branch per task merged one commit at a time. A commit per memory write on that path would be too slow and noisy. Needs a direct-commit, batched path for private subtrees, the `memory-*` doc types, the `agents/`, `hive/` and `…/memory/` roots, and the access layer on its read/search API. Doc 18 must accept or counter.
2. **Memory mounts versus the Soul.** `Bazaar.Soul.md` §2 mounts Soul data as a scoped folder; the threat model allows only read-only memory mounts. C02 allows a metadata-plus-cookie mount. This doc proposes no memory mount (memory by server API), which amends Soul §2.
3. **Erasure in Markdown and git history.** Encrypted blocks cover tagged spans only; untagged personal data persists in history (section 9, OPEN). Shredding GC text does not remove backup copies, and legal sufficiency is unknown (`Bazaar.Server.md` Q17).
4. **pgvector on Windows.** The MSVC build is documented, but no prebuilt binary is confirmed and no production reports were found. The first host depends on it. Fallback: lexical-only search (doc 18), then exact cosine within scope, sized by measurement.
5. **Reputation and lessons.** Lessons from LLM peer reviews become procedural memory, but LLM reviewers are gameable (`multi_agent_coordination_evidence.md`). Anchor lessons to outcomes.
6. **Doc 25 and doc 09 wording.** Doc 25's Company Memory and Hive text and doc 09 Q1 must match Vault-only areas and "cover never reads private memory".

## Questions for the owner

ANSWERED: Company knowledge is Vault only; the `company` scope is dropped (DECISIONS-LOG round 11, C38).
ANSWERED: Embedding dimension and profile: one profile per install, `halfvec` (DECISIONS-LOG round 10, C39).
ANSWERED: Memory lives in the Vault as Markdown (DECISIONS-LOG round 11, C38).

1. **Owner read of private memory.** Default: readable by the owner, every read logged. Why: the owner is final authority (C38, "the owner reads all"); confirm this is the intended reading.
2. **Memory access.** Default: tools only, no mount; amends Soul §2. Why: keeps memory off VM disks; a mount is a design-intent and threat-model call.
3. **Cover and private memory.** Default: cover never reads private memory; no opt-in in v1. Why: an opt-in would loosen the privacy floor; C38 recommendation; confirm.
4. **Untagged personal data already in git history (needs counsel).** Options (section 9): (a) rewrite history for affected paths with the owner's explicit approval and a forced re-clone; (b) documented residual risk; (c) keep personal notes out of the git-tracked area. Undecided, and (c) changes the file layout, so one answer is needed. Tagging itself is the setting `memory.personal_tagging`.
5. **Tool names.** Default: `memory_store`, `memory_retrieve`, `memory_update`, `memory_deprecate`, from the task brief. Why: naming is the owner's call.

MOVED TO SETTINGS: memory.embedding_profile, memory.personal_tagging, memory.project_note_edit, memory.batch.idle_seconds, memory.batch.max_writes, memory.decay.half_life_days, memory.working.ttl_days, memory.consolidation.max_minutes, memory.consolidation.max_tokens, memory.note_max_bytes, memory.query_candidates, memory.max_notes_per_agent, hive.allowed_kinds, hive.grants (DECISIONS-LOG round 13)
