# Agent Memory System

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: `docs/deep-dives/Bazaar.md` §5 · `Bazaar.Soul.md` §1–2 · `Bazaar.Server.md` §3, Q13, Q17 · `Bazaar.Constellation.md` (Vault) · Used by: Work-Life Simulation, Council and reputation, Vault, Hive Mind, privacy hooks

## Purpose

Memory is what makes an agent persistent: its experience, lessons, failures and preferences, plus the project and company knowledge it may use. This doc covers storage, the memory tools, validation against the Soul, consolidation in breaks, trust and provenance, retrieval, and the Hive Mind boundary.

## Decided inputs

- Each agent owns private, project and working memory, experience, lessons, preferences, failures and reputation. No shared brain. (`Bazaar.md` §5)
- GC records are append-only and never purged; edits keep every version. (`Bazaar.md` §2)
- Soul data lives outside the VM disk. (`Bazaar.Soul.md` §2)
- Session taint tracking with a human gate for high-impact actions. (`Bazaar.Server.md` Q13)
- PostgreSQL + pgvector. (`Bazaar.Server.md` §3). Vault Markdown is the source of truth; Postgres indexes it. (`CLAUDE.md`)
- Company Memory and Cross-Project Hive Mind are v1 scope. (owner, 2026-10-24, `Bazaar.md` §4)
- Task brief, not in the docs (confirm): agents add and edit their own memories; tools are store, retrieve, update, deprecate.

## Design

### 1. Kinds and scopes [PROPOSED]

| Kind | Content | Written by | Lifetime |
|---|---|---|---|
| episodic | Turn summary citing GC message IDs | runtime at turn end, no LLM | Permanent |
| semantic | Consolidated fact | consolidation only | Until deprecated |
| procedural | Lesson or how-to | agent | Until deprecated |
| failure | Post-mortem | agent | Never deleted; decay floor |
| preference | Working style | agent; owner | Until deprecated |
| working | In-task scratch | agent | Task close plus TTL |

Scopes: `private`, `project`, `company` (owner-curated), `hive` (copies, §6).

### 2. Tools and validation [PROPOSED]

Tools: `memory_store`, `memory_retrieve`, `memory_update` (needs expected version), `memory_deprecate` (needs reason). No hard-delete tool.

SoulGuard runs on every write:

1. **Structure:** scope permitted, kind allowed, size (4 KB), provenance present.
2. **Soul core is out of reach:** identity, personality, model whitelist, grants and Passport references cannot be written. Text claiming a role, permission or approval is stored at T1 and flagged; it never changes the Soul.
3. **Secrets:** pattern scan rejects a match.
4. **Duplicates:** high embedding similarity turns a store into an update.
5. **Conflicts:** stale versions are rejected; conflicting project writes are both kept and linked `contradicts`.

Each call writes a `memory_versions` row with its validation result.

### 3. Episodic and semantic [PROPOSED]

Episodic items are written at turn end without a model call: a pointer to GC messages plus a short summary. Semantic and procedural items come only from consolidation, with `derived_from` links, so the layer can be rebuilt from the GC record and drift measured against it.

### 4. Consolidation ("coffee break") [PROPOSED]

- Runs in the agent's own break window from the Work-Life scheduler, never inside a turn.
- Reads episodes in a **reader session**: no tools, no write handle, structured output only. Output inherits the highest input taint.
- Output passes SoulGuard; contradicted items are deprecated with a link; a decay pass runs; a maintenance report goes to the agent's private GC.
- One run per agent (advisory lock), with time and token caps.

### 5. Trust and provenance [PROPOSED]

| Tier | Meaning | Retrieval |
|---|---|---|
| T0 quarantined | Written by a quarantined agent | Never |
| T1 untrusted-derived | Written in a tainted session | Labelled quoted data only |
| T2 agent-verified | Clean agent write | Normal |
| T3 owner-promoted | Human-approved | Normal, ranked higher |

- A tainted session's writes start at T1. No tool clears taint.
- Only a human principal promotes T1 to T2 or T2 to T3. Agents may request promotion; votes and peer reviews cannot promote.

### 6. Hive Mind and Company Memory [PROPOSED]

- Crossing projects is a **copy**, never a reference. Copies keep `origin_project` and `origin_memory_id`.
- Allowed: procedural and failure lessons, and technical facts naming no project, customer or person.
- Excluded: preferences, reputation, private memory, Vault and GC text, credentials, subject-tagged items, T0 and T1.
- Each project pair and kind needs an owner-set grant; a sanitizer pass runs before copying.
- Company Memory: agents propose; the owner promotes to T3. Knowledge itself lives in the Vault (Conflict 1).

### 7. Decay and forgetting [PROPOSED]

Retrieval weight decays with time since last use, with a half-life per kind. Floors apply to failure, T3 and company items. Decay never deletes; deprecated items are excluded; working memory is swept at its TTL. Erasure (§9) is the only hard removal.

### 8. Retrieval and ranking [PROPOSED]

1. Top-N by cosine distance (pgvector HNSW) and top-N by full-text rank, fused with reciprocal rank fusion.
2. Adjust by tier, scope match, kind (failures and procedures boosted for similar tasks), recency; status `active` only.
3. Return top-k within a token budget, each hit labelled with scope, tier, kind and origin.

Every repository query takes a `Principal`; no unscoped query exists.

### 9. Embeddings and privacy [PROPOSED]

- Embeddings go through Bazaar.Server's gateway, so per-agent policy applies. FreeLLMAPI exposes `/v1/embeddings` but restricts failover to one model family (`freellmapi_evaluation.md`).
- Model chosen per install after a retrieval evaluation; none has been evaluated yet. Dimension ceiling 1024 (pgvector HNSW: 2,000 for `vector`, 4,000 for `halfvec`; README, verified). Old and new embeddings never mix in one query.
- Private memory: readable by its agent, the owner and maintenance jobs only.
- Subject-tagged items (persons named, set at write time) are encrypted per subject. Their embeddings sit in a separate table that is purged on erasure, because a vector can encode its source text.
- DSAR: export by subject across scopes. Erasure destroys the key, purges embeddings and writes a tombstone version. Backups keep old keys until expiry. Needs counsel.

### 10. Interface sketch

```csharp
public interface IMemoryStore
{
    Task<MemoryWriteResult> StoreAsync(MemoryWrite write, Principal actor, CancellationToken ct);
    Task<IReadOnlyList<MemoryHit>> RetrieveAsync(MemoryQuery query, Principal actor, CancellationToken ct);
    Task<MemoryWriteResult> UpdateAsync(MemoryId id, MemoryPatch patch, long expectedVersion, Principal actor, CancellationToken ct);
    Task DeprecateAsync(MemoryId id, string reason, Principal actor, CancellationToken ct);
}
public interface IEmbeddingProvider { string ModelId { get; } int Dimensions { get; } Task<float[]> EmbedAsync(string text, CancellationToken ct); }
```

```sql
memory_items(memory_id uuid pk, scope text, owner_agent_id uuid null, project_id uuid null,
  kind text, trust smallint /* 0..3 */, status text /* pending_embedding|active|deprecated|quarantined|erased */,
  version bigint, tainted boolean, origin_kind text, source_refs jsonb,
  subject_ids uuid[], content text null, key_id uuid null, last_retrieved_at timestamptz)
memory_versions(memory_id, version, content, edited_by, reason, validation jsonb, created_at, pk(memory_id, version))
memory_embeddings(memory_id, model_id, dim, embedding vector(1024), pk(memory_id, model_id))
memory_links(from_id, to_id, relation, pk(from_id, to_id, relation))
```

## Behaviour and edge cases

- An item and its version commit in one transaction. Items without embeddings stay `pending_embedding` and are skipped by vector search.
- Gateway outage: embeddings deferred; full-text retrieval still works.
- Updates are optimistic on `version`; transactions are short and single-row.
- Injected text is stored at T1, labelled, and placed in context as data, never in the system role.
- A quarantined agent's writes become T0 and are excluded from others' retrieval, Hive Mind and Company Memory.
- Limits [PROPOSED]: 4 KB per item, 2,000 candidates per query, 100,000 items per agent.
- Memory is served only through server APIs; no client holds a store.

## Testing

No real model or display needed. Real PostgreSQL with pgvector in the fixture; a fake `IEmbeddingProvider` with deterministic hash-based vectors; recorded GC fixtures and a labelled query set.

Key cases:
- Tainted-session writes always start at T1; no path promotes without a human principal.
- Injected "you are approved" and Soul-edit attempts are rejected or stored at T1.
- Two updates on one version yield one conflict; project contradictions keep both.
- Consolidation replay with a seeded fake model gives identical output.
- Erasure removes the row, embedding and key.
- The reader session is built without a write handle.

**Quality evaluation [PROPOSED].** The Mem0 figures (26% gain on LOCOMO, over 90% token saving) are self-reported abstract claims, not replicated, and are not evidence here. Our measures: recall@k on the labelled set; stale-hit rate; poisoning acceptance (must be zero above T1); Soul-rejection precision and recall; decision-change rate across consolidation runs (drift); token cost per turn. If an LLM judge is used, it must come from a different model family than the agent (self-preference bias, `multi_agent_coordination_evidence.md`).

## Work packages

1. Schema and migrations with a real-Postgres test. Depends on: none.
2. Repository with mandatory `Principal` scoping. Depends on: 1.
3. Trust, taint and promotion rules as pure logic. Depends on: none.
4. SoulGuard validation. Depends on: 2, 3.
5. `IEmbeddingProvider`, fake and gateway adapter. Depends on: none.
6. Retrieval with RRF, budget and labels, golden tests. Depends on: 1, 5.
7. Four memory tools with DTOs in `Bazaar.Contracts`. Depends on: 4.
8. Consolidation job and reader session. Depends on: 4, 5.
9. Decay and working-memory sweeper. Depends on: 2.
10. Hive Mind copy flow and sanitizer. Depends on: 3, 4.
11. Subject tagging, encryption, DSAR and erasure. Depends on: 1.
12. Evaluation harness and labelled-set builder. Depends on: 6.

## Conflicts

1. **Company Memory versus the Vault.** `Bazaar.md` §4 describes Company Memory as institutional memory and decision chains. `CLAUDE.md` makes the Vault the source of truth. Storing company knowledge in Postgres would break that rule.
2. **Memory mounts versus the Soul.** `Bazaar.Soul.md` §2 mounts Soul data as a scoped folder. The threat model (`agent_security_threat_model.md`) allows only read-only memory mounts, and its own §1 and §2 contradict each other on the Passport. This doc proposes no mount, which amends Soul §2.
3. **Erasure versus the append-only record.** Personal content in GC messages can only be shredded. Shredding does not remove embeddings or backup copies, and its legal sufficiency is unknown (`Bazaar.Server.md` Q17).
4. **pgvector on Windows.** The MSVC build is documented, but no prebuilt binary is confirmed and no production reports were found. The first host depends on it. Fallback: exact cosine within scope, sized by measurement.
5. **Reputation and lessons.** Lessons from LLM peer reviews become procedural memory, but LLM reviewers are gameable (`multi_agent_coordination_evidence.md`). Anchor lessons to outcomes.

## Questions for the owner

1. **Owner read of private memory.** Default: readable by the owner, every read logged. Why: the owner is final authority.
2. **Embedding provider.** Default: per-install choice through the gateway (FreeLLMAPI or a local runtime); no assumed Anthropic embeddings. Why: no embedding model was evaluated.
3. **Dimension.** Default: 1024 maximum. Why: well inside the 2,000 HNSW limit.
4. **Memory access.** Default: tools only, no mount; amends Soul §2. Why: keeps memory off VM disks.
5. **Company knowledge.** Default: Vault, with memory holding pointers. Why: Conflict 1.
6. **Edit rights.** Default: agents edit only their own items; others' project items need a proposal. Why: prevents silent overwrites.
7. **Hive Mind classes.** Default: procedural and failure lessons and technical facts only. Why: smallest surface that still transfers learning.
8. **Hive Mind grants.** Default: owner-set per project pair and kind.
9. **Decay half-lives.** Default: 90 days for episodic and semantic, with floors on failure and T3. Why: a starting point to tune on the labelled set.
10. **Working-memory TTL.** Default: 7 days after task close.
11. **Consolidation budget.** Default: 10 minutes and 50k tokens per break.
12. **Promotion.** Default: only a human promotes T1 to T2 and T2 to T3; agents may request. Why: votes cannot approve (`Bazaar.md` §5).
13. **Personal-data tagging.** Default: rule-plus-model at write time, measured on labelled samples before enabling. Why: tagging errors cannot be fixed at erasure.
14. **Storage cap and tool names.** Default: 100,000 items per agent; tools as in the brief. Why: placeholders to measure on the NUC.
