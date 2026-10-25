# Agent Memory System

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 10
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
| company | any memory type | `company/memory/…` | agents propose; owner promotes | Until deprecated |
| hive | `memory-hive` | `hive/<kind>/<slug>.md` | copy flow (section 6) | Until deprecated |

Scopes are therefore areas: `private` (the agent's own `agents/<agent>/`), `project`, `company`, `hive`. There is no separate company scope in the database; a company fact is a note under `company/`, and its owner-promoted tier is front matter. Episodic notes are tiny, so a turn summary costs one small file; they are written in batches (section 3). Working notes are deleted by the sweeper at TTL, and git history keeps them.

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

A commit per memory write would flood the repo. The memory write path keeps a per-agent in-memory queue and flushes one commit per agent when the first of these happens: end of the turn, 30 seconds idle, or 20 queued writes. The writer validates all items, writes the files, makes one commit with trailers (`Bazaar-Agent`, `Bazaar-GC`, `Bazaar-Task`, `Bazaar-Memory-Ops`) and emits `(oldSha, newSha)` to the indexer. Reads within the same turn see the queue first, so a store followed by a retrieve is consistent. Queued writes survive a restart through a small spool table (`memory_spool`) replayed at startup. Consolidation (section 4) is one commit per run. Batch thresholds are [PROPOSED] defaults to measure.

### 4. Episodic and semantic [PROPOSED]

Episodic notes are written at turn end without a model call: a pointer to GC messages plus a short summary. Semantic and procedural-from-consolidation notes come only from consolidation, with `derived_from` links, so the layer can be rebuilt from the GC record and drift measured against it.

### 5. Trust and provenance [PROPOSED]

Front matter carries both scales (C20): `source_trust` is doc 15's provenance enum, `trust` is the promotion tier.

| Tier | Meaning | Retrieval |
|---|---|---|
| T0 quarantined | Written by a quarantined agent | Never |
| T1 untrusted-derived | Written in a tainted session | Labelled quoted data only |
| T2 agent-verified | Clean agent write | Normal |
| T3 owner-promoted | Human-approved | Normal, ranked higher |

Boundary mapping [PROPOSED; doc 15 confirms the enum names]: a write from a hard-tainted session or with `source_trust` external gets T1; a quarantined `source_trust` gets T0; a clean agent write gets T2; only a human principal sets T3. A tainted session's writes start at T1 and no tool clears taint. Only a human promotes T1 to T2 or T2 to T3; agents may request, and votes and peer reviews cannot promote. Promotion is a commit by the owner's identity, so the audit trail is in git.

### 6. Hive Mind and Company Memory as Vault areas [PROPOSED]

- Company Memory is `company/memory/`; Hive Mind is `hive/`. Both are Vault only (C38). Agents propose; the owner promotes to T3.
- Crossing projects is a **copy** note, never a link target of private text. Copies keep `origin_project` and `origin_note` in front matter.
- Allowed: procedural and failure lessons, and technical facts naming no project, customer or person. Excluded: preferences, reputation, private memory, GC text, credentials, subject-tagged items, T0 and T1.
- A sanitizer pass runs before copying. Grants per project pair and kind are owner-set; doc 25 Q10 (items reviewed for 30 days, then automatic under the grant) is adopted per DECISIONS-LOG C38 recommendation, subject to the owner's confirmation in Q6.

### 7. Consolidation ("coffee break") [PROPOSED]

- Runs in the agent's own break window from the Work-Life scheduler (doc 09), never inside a turn.
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

**Cover reads primary (doc 09).** Cover reads the handover note and shared project memory, never private memory. Anything the primary wants the cover to know is written to the handover note or a project note. Per-agent opt-in to private reads is not offered in v1 (DECISIONS-LOG C38 recommendation; the owner answered C38 only in general terms, so Q8 keeps it open). Doc 09 Q1 should match.

### 9. Embeddings, personal data and erasure [PROPOSED; erasure OPEN]

- Embeddings go through Bazaar.Server's gateway (doc 05), so per-agent policy applies. FreeLLMAPI exposes `/v1/embeddings` but restricts failover to one model family (`freellmapi_evaluation.md`).
- One profile per install, `halfvec`; the dimension is fixed at setup. No model has been evaluated yet; choose after a retrieval evaluation. Embeddings of different profiles never mix in one query. pgvector HNSW limits: 2,000 for `vector`, 4,000 for `halfvec` (README, verified).
- Personal data (C01). Spans naming a person are tagged at write time (Q7) and stored as an **encrypted block** inside the note: a fenced block carrying `subject` id, key id and ciphertext, with the key held in doc 14's secret/key store. The indexer never sees plaintext of these blocks and never embeds them, so no vector encodes them. Retrieval shows "[encrypted personal data]" unless the Principal may decrypt. DSAR export: decrypt by subject across areas and list the notes.
- Erasure destroys the subject key. Ciphertext stays in the files and in git history but is unreadable; the note gets a tombstone line. Structure, authorship and timestamps stay. Backups keep old keys until expiry, and backup retention must equal the erasure window (doc 33). Legal sufficiency needs counsel.
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

## Behaviour and edge cases

- A note and its index rows are consistent through the indexer's catch-up from `indexed_commit` (doc 18). Notes without embeddings are skipped by vector search.
- Gateway outage: embeddings deferred; full-text retrieval still works.
- Updates are optimistic on `memory.version`; git history is the version log.
- Injected text is stored at T1, labelled, and placed in context as data, never in the system role.
- A quarantined agent's writes become T0 and are excluded from others' retrieval, Hive Mind and Company Memory.
- Limits [PROPOSED]: 4 KB per note, 2,000 candidates per query, 100,000 notes per agent. A very large per-agent file count may slow git; measure on the NUC.
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
12. Subject tagging, encrypted blocks, DSAR and erasure. Depends on: 1; key store from doc 14.
13. Evaluation harness and labelled-set builder. Depends on: 7.

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

1. **Owner read of private memory.** Default: readable by the owner, every read logged. Why: the owner is final authority.
2. **Embedding provider.** Default: per-install choice through the gateway (FreeLLMAPI or a local runtime); no assumed Anthropic embeddings. Why: no embedding model was evaluated.
3. **Memory access.** Default: tools only, no mount; amends Soul §2. Why: keeps memory off VM disks.
4. **Edit rights.** Default: agents edit only their own notes; others' project notes need a proposal. Why: prevents silent overwrites.
5. **Batching thresholds.** Default: flush at turn end, 30 seconds idle or 20 writes; consolidation is one commit. Why: starting points to measure.
6. **Hive Mind classes and grants.** Default: procedural and failure lessons and technical facts only; owner-set grants per project pair and kind; copies reviewed 30 days then automatic (doc 25 Q10). Why: smallest surface that still transfers learning.
7. **Personal-data tagging and untagged leaks.** Default: rule-plus-model tagging at write time, measured on labelled samples before enabling. OPEN: pick a policy for untagged personal data already in git history (section 9 options a to c). Why: tagging errors cannot be fixed by key destruction.
8. **Cover and private memory.** Default: cover never reads private memory; no opt-in in v1. Why: C38 recommendation; confirm.
9. **Decay, TTL and consolidation budget.** Default: 90-day half-life for episodic and semantic with floors on failure and T3; working notes 7 days after task close; 10 minutes and 50k tokens per break. Why: starting points to tune on the labelled set.
10. **Promotion and storage cap.** Default: only a human promotes T1 to T2 and T2 to T3; agents may request; 100,000 notes per agent; tool names as in the brief. Why: votes cannot approve (`Bazaar.md` §5); caps are placeholders to measure on the NUC.
