# Company Systems: Knowledge and Metrics

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 14
Depends on: 01-gc-data-model.md · 02-wire-protocol-and-sync.md · 04-postgres-schema-and-migrations.md · 06-agent-runtime.md · 07-soul-system.md · 08-memory-system.md · 09-work-life-simulation.md · 10-council-and-voting.md · 11-reputation-and-peer-review.md · 12-kanban-and-dependencies.md · 15-security-taint-and-approvals.md · 18-vault.md · 21-github-integration.md · 23-observatory-and-usage.md · Used by: 19-constellation-and-git-solar-system.md · 24-company-systems-core.md · 27-client-architecture.md · 28-windows-client.md · 29-android-client.md

## Purpose
Seven v1 company systems that turn the GC record, the Vault and the project repos into measured, navigable and reusable company knowledge. All of them read from the record and write derived or staged output. None is a coordination channel, and none can grant an agent authority.

## Decided inputs
- All seven are v1 scope (Bazaar.md §4, owner 2026-10-24). Their designs below are [PROPOSED].
- No master orchestrator; GCs are the coordination system; other systems serve GCs and never replace them (Bazaar.md §1–2).
- GC history is append-only; edits keep every version, in `gc_event`, `gc_message` and `gc_message_version` (01; DECISIONS-LOG round 1, C09).
- Resource Economy is REJECTED. No money budgets in any system here (Bazaar.md §4).
- Each agent owns its private memory; there is no shared brain (Bazaar.md §5).
- The Vault holds every durable written artifact, including memories, decisions, Twin pages and the Archive. Postgres holds the GC log, structured state and indexes over Vault files (DECISIONS-LOG round 11, C38). Decision records are `decisions/NNNN-slug.md`, written only through `VaultWriter` (18; round 7, C24). Git access goes through `IGitProvider` (round 3, C12).
- Derived stores are fed by GC events over Postgres NOTIFY, with no Valkey or Garnet dependency (04; round 1, C07). Thinking GC text is never published; vote reasoning is withheld until the joint reveal (10, 26; round 3, C11).
- Install data never leaves its install; Soul exports carry no memories, accounts or reputation (Bazaar.md §1, §4).
- Reputation combines peer reviews and hard outcomes (Bazaar.md §5). Weights are OPEN.
- Promotions, hiring (Talent Pool) and retirement run as Council votes. A unanimous vote passes; a split or tied vote on a bigger change escalates to the owner (DECISIONS-LOG round 9, C32).

## Design

| System | Data sources | Storage | Update trigger | Client shows |
|---|---|---|---|---|
| **Health and Analytics** | CI results, coverage and analyzer output, package audit (21); Kanban states (12); `gc_event` structured kinds such as blocked, vote, interrupt (01, 24) | `metric_samples(project_id, metric, bucket, value, source_ref)` with daily rollups; recomputable | CI completion; nightly rollup | Per-project panel: tech debt, coverage trend, build pass rate, blocked share. Per-agent view: display only, framed as "what would help the company" |
| **Digital Twin** | Repo and project files, lock files (21, via `IGitProvider`); Vault index (18); PR, release and CI events; Kanban graph (12) | `twin_facts(project_id, kind, subject, value, confidence, status, source_refs[])`: a rebuildable Postgres index, never authoritative. Prose pages are Vault Markdown written through `VaultWriter` (C11) | PR merged, tag created, CI done, Kanban edit (debounced 10 min), via GC events; weekly full rebuild | Project card: architecture (nodes from 19), owners, dependencies, bottlenecks, release status, roadmap progress |
| **Bazaar Archive** | `gc_message` and `gc_event` of the project's GCs (01); decision records in `decisions/` (18); merged PR records | Markdown chapters in Vault `archive/`, written through `VaultWriter` under the `bazaar-archive` identity; chapter index in Postgres | Release tag; monthly; on demand | Read-only book with chapter navigation. Every sentence links to its GC entry |
| **Company Memory** | Vault `decisions/` front matter (`status`, `supersedes`, `decidedIn`) (C24) | Company knowledge pages in Vault `company/memory/` (C38). `decision_links(from_doc, to_doc, relation, status)`: a derived Postgres index rebuilt from the Vault | Vault commit (`VaultWriter` emits old and new SHA) | Decision chain: what replaced what, why, and the GC message that decided it |
| **Hive Mind** | Procedural and failure lessons from private memory of agents in project A (08) | Copies as Vault Markdown under `hive/`, with front matter `origin_project`, `origin_memory`, `trust` and `origin_kind` (08 §6, C11). Grants in `hive_grants(from_project, to_project, kind, granted_by)` in Postgres | Owner grant change; nightly batch of new eligible lessons | Owner grant matrix; each copy labelled with its origin project |
| **Talent Pool** | Soul templates offered by agents (07) | `talent_templates(template_id, version, manifest jsonb, content_sha, status, source_soul_id)`; manifest only: prompt, tool allowlist, model whitelist. No memory | Contribution submitted; Council vote (10, C32) | Library with version diffs; "instantiate" creates a new Soul through 07 |
| **Genetic Memory Transfer** | Child subagent's trace (06) and its proposed lessons | Staged Vault Markdown candidates (08 staging area), not retrievable until accepted; `origin_kind = agent`, with the child's taint state as the trust cap | Subagent finishing, before it ends | Review queue: candidate, trace link, accept, edit or reject |

### Boundaries [PROPOSED]
- **Company Memory, Vault and GC.** GC entries are what was said and decided: evidence. The Vault holds knowledge and decision records. Company Memory holds only chain edges and cached status, rebuilt from the Vault. Every fact is a Vault document, so there is one place to edit.
- **Hive Mind and 08.** 08 §6 owns the mechanism: copy, sanitizer, SoulGuard. This doc owns the policy: which kinds may cross, the grant matrix and the owner UI. A copy is never a reference into another project's Vault or GC text.
- **Analytics and reputation.** Per-agent metrics have no write path into reputation (11), the Work-Life schedule (09), votes or Soul state. Enforced by a build-time reference check (see Testing).
- **Digital Twin and Constellation.** The Twin emits project nodes and edges; 19 owns the layout and drawing.

### Trust and crossing rules [PROPOSED]
- Every fact, chapter, metric note and copy carries `trust` (the 08 tier, T0 to T3) and `origin_kind` (the 15 provenance enum). It also carries `source_refs` (08 §5). Derived output takes the highest trust of its inputs (08 §4).
- Anything crossing agents or projects (Hive copy, Talent template, GMT candidate, Twin fact, Archive chapter) is data labelled by origin, never instruction. Only an owner promotion raises trust.
- Talent templates hold prompts and tool grants, which is executable intent. Importing one is quarantined until a Council vote passes (C32). Instantiating one with any tool grant is Tier 2 (07, 15).
- Prose (Twin summaries, chapter text, GMT candidates) is written in a reader session with no tools. Reader sessions are the reader/actor split's primitive (15; DECISIONS-LOG round 6, C18, Claude's reading, to confirm). A sentence without a valid `source_ref` is dropped by the validator.
- Nothing in this doc crosses installs. Talent templates leave an install only through the optional Soul Marketplace, which excludes memory and metrics.

## Data and interfaces

```csharp
public interface ITwinBuilder { Task RebuildAsync(ProjectId project, CancellationToken ct); }
public interface IHiveGrantPolicy { Task<bool> MayCopyAsync(LessonRef lesson, ProjectId target, CancellationToken ct); }
public interface ITalentPool { Task<TemplateSubmitResult> SubmitAsync(SoulManifest manifest, Principal actor, CancellationToken ct); }
```

```json
{ "fact": "project-api-depends-on", "subject": "Bazaar.Server", "value": "Npgsql 10.x",
  "confidence": 0.95, "status": "active", "trust": "T2", "origin_kind": "repo",
  "source_refs": [{ "kind": "repo-file", "path": "Directory.Packages.props", "commit": "<sha>" }] }
```

The example's `origin_kind` value is a placeholder until 15 confirms its enum.

## Behaviour and edge cases
- Every derived store can be rebuilt from GC entries, the Vault and repos. The same sources give the same fact IDs and rollups. Deterministic extractors come first; the model writes prose only.
- A source change marks the affected fact `stale` until rebuilt. Conflicting sources lower `confidence`; they never silently overwrite.
- A missing CI run is a gap, not a zero. Coverage alerts compare only within the same toolchain.
- Chapters are versioned. A regenerated chapter links its predecessor. Nobody hand-edits `archive/`.
- Hive: revoking a grant sets the copies' front matter to `deprecated` through `VaultWriter`, never deletes them. Copies never promote themselves. The sanitizer rejects text naming a project, customer, person or secret pattern.
- GMT candidates are invisible to the parent's retrieval until accepted. A candidate from a tainted child stays T1.
- Talent: a template whose model is outside the whitelist is refused at instantiation.
- Clients read cached views only, over the 02 socket and HTTP surface. All computation runs on the server.

## Testing
No real model or display. Real Postgres (Testcontainers) for rollups, rebuilds, chains and grants; a fake gateway returns scripted prose. Key cases:
- Rebuild from identical sources yields identical fact IDs and rollups.
- A missing CI run produces a gap, not a zero.
- A Hive copy of a T1 lesson, Vault text, or a subject-tagged item is refused. Revoking a grant deprecates copies.
- A prose sentence without a valid `source_ref` fails the validator.
- Build-time reference check: the metrics assembly cannot reference reputation, scheduling or voting code.
- A Talent template containing a memory file, or a non-whitelisted model, is rejected.
- A GMT candidate from a tainted child is T1 and not retrievable before acceptance.
- Avalonia.Headless: decision chain and archive views render an unresolved reference as an explicit "missing source" link.

## Work packages
1. Metric store, rollup job, CI and coverage collectors with fixtures. Needs 04, 21.
2. Health rules and project panel queries. Needs 1.
3. Twin fact schema and deterministic extractors (dependencies, owners, releases). Needs 04, 21.
4. Twin prose step in a reader session with the ref validator. Needs 3, 05, 15.
5. Decision-chain index from Vault front matter. Needs 18.
6. Archive generator, chapter validator, Vault write through `VaultWriter`. Needs 1, 5.
7. Hive grant table, Vault copy flow and sanitizer on 08 §6 scopes. Needs 08, 18.
8. Talent Pool store, manifest validator, Council review hook. Needs 07, 10, 11.
9. GMT staging, child-end hook in 06, review queue. Needs 06, 08, 15.
10. Client panels for all seven. Needs 27, 28, 29. Packages 1 to 9 run in parallel; 10 follows each one.

## Conflicts
1. **Source of truth.** The Vault is the source of truth (18), yet Archive and Twin output is generated. Derived text risks being cited as authority. Mitigation: generated folders are labelled and never hold decision records.
2. **Morale and per-agent metrics versus reputation.** "Display only" holds only while no feature reads the metric. Reputation built from LLM reviews is open to inflation (multi_agent_coordination_evidence, Key Q3). LLM-scored morale from agent text is exposed to self-preference bias (arXiv 2410.21819, abstract-level verified).
3. **Subagent lessons.** 06 says only the parent posts, 07 says candidates need architect or owner review, and 15 must confirm its taint rules (C06 softened agent-text taint) still stop a tainted child's lessons from being trusted. Untested together. The `origin_kind` enum in 15 has no `subagent` value; this doc maps children to `agent`. 15 to confirm.
4. **Evidence strength.** Cross-agent contamination is inferred from OWASP ASI06 and ASI07 (secondary source), not measured (agent_security_threat_model §4). Hive Mind and Archive rest on that inference.
5. **Talent eligibility.** Talent review now runs as a Council vote (C32), but reputation weights are OPEN (Bazaar.md §5) and "bigger changes" is undefined (C32 FOLLOW-UP). 07 and 10 must use the same definition.
6. **Erasure in Markdown.** C01 erasure is per-person encrypted fields in Postgres. Archive chapters, Hive copies and Vault decision files hold GC text in Markdown and git history, where key destruction does not reach. Needs a policy from 18 and 33 (C38 consequence, FOLLOW-UP).

## Questions for the owner
1. ANSWERED: company knowledge lives in the Vault only; no free-standing company facts in Postgres (DECISIONS-LOG round 11, C38).
2. Who sees per-agent metrics? Default: the owner and that agent's own Soul; not peers.
3. Which morale signals count? Default: project-level only (blocked share, reopened tasks, vote reversals). No sentiment scoring of agent text.
4. Health warning thresholds? Default: coverage drop over 2 points on the same toolchain; build failure streak of 3 or more. Both are placeholders.
5. Twin scope in v1? Default: repos linked through 21 only, with the fact kinds listed above.
6. Twin storage? Default: the fact index in Postgres; prose pages as Vault Markdown (C11, Claude's reading, to confirm). No separate snapshot.
7. Archive cadence? Default: release tags plus monthly.
8. Are DMs and member-human text excluded from the Archive? Default: excluded unless the owner opts a GC in.
9. Hive Mind grants at launch? Default: no grants; the owner sets pair-and-kind grants. Allowed kinds: procedural and failure lessons only.
10. Hive batches auto-copy under a grant, or get reviewed? Default: reviewed for 30 days, then automatic under the grant. Pending owner confirmation.
11. Talent: which contributions are "bigger changes" that escalate to the owner on a split or tied vote? Default: any new agent instantiation and any tool grant (DECISIONS-LOG round 9, C32 FOLLOW-UP; shared with 07 and 10).
12. GMT approvals: architect for lessons, owner for tool grants or charter text. Default as stated (matches 07 Q8).
13. Rename "Hive Mind" to "Cross-Project Memory" to signal copies, not a shared brain? Default: keep the name, and show "copies only" in the UI.
14. Metric retention? Default: raw samples 90 days; daily rollups kept indefinitely.
15. Should the Archive include the stated reasons from Council votes (10)? Default: yes, only after the joint reveal, per the auditable-reasoning standing ask (Bazaar.md §5; C11).
