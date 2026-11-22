# Company Systems: Knowledge and Metrics

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: 01-gc-data-model.md · 02-wire-protocol-and-sync.md · 03-server-host-and-providers.md · 04-postgres-schema-and-migrations.md · 06-agent-runtime.md · 07-soul-system.md · 08-memory-system.md · 09-work-life-simulation.md · 10-council-and-voting.md · 11-reputation-and-peer-review.md · 12-kanban-and-dependencies.md · 15-security-taint-and-approvals.md · 18-vault.md · 21-github-integration.md · 23-observatory-and-usage.md · Used by: 19-constellation-and-git-solar-system.md · 24-company-systems-core.md · 27-client-architecture.md · 28-windows-client.md · 29-android-client.md

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
- Promotions, hiring (Talent Pool) and retirement run as Council motions. A unanimous vote passes; a split or tied vote on a bigger change escalates to the owner (DECISIONS-LOG round 9, C32; Talent hiring is a `hire` motion, 10).
- Configurability rule (DECISIONS-LOG round 13, owner): plain value and policy choices here are install or project settings with safe defaults, not owner questions. See Settings.
- Answered (round 11, C38): company knowledge lives in the Vault only; no free-standing company facts in Postgres.

## Design

| System | Data sources | Storage | Update trigger | Client shows |
|---|---|---|---|---|
| **Health and Analytics** | CI results, coverage and analyzer output, package audit (21); Kanban states (12); `gc_event` structured kinds such as blocked, vote, interrupt (01, 24) | `metric_samples(project_id, metric, bucket, value, source_ref)` with daily rollups; recomputable | CI completion; nightly rollup | Per-project panel: tech debt, coverage trend, build pass rate, blocked share. Per-agent view: display only, framed as "what would help the company", visible per `analytics.agent_metrics_visibility` |
| **Digital Twin** | Repo and project files, lock files (21, via `IGitProvider`); Vault index (18); PR, release and CI events; Kanban graph (12) | `twin_facts(project_id, kind, subject, value, confidence, status, source_refs[])`: a rebuildable Postgres index, never authoritative. Prose pages are Vault Markdown at `twin/<project>/<page>.md` [PROPOSED, folder name from 18], written through `VaultWriter` (C11) | PR merged, tag created, CI done, Kanban edit (debounced 10 min), via GC events; weekly full rebuild | Project card: architecture (nodes from 19), owners, dependencies, bottlenecks, release status, roadmap progress |
| **Bazaar Archive** | `gc_message` and `gc_event` of the project's GCs (01); decision records in `decisions/` (18); merged PR records | Markdown chapters in Vault `archive/`, written through `VaultWriter` under the `bazaar-archive` identity; chapter index in Postgres | Per `archive.chapter_cadence` (release tag, monthly, on demand) | Read-only book with chapter navigation. Every sentence links to its GC entry |
| **Company Memory** | Vault `decisions/` front matter (`status`, `supersedes`, `decidedIn`) (C24); the company knowledge tier: proposals by agents and the distillation job over project lessons, failures, decision records and retrospective notes (08 §6.3) | Company knowledge notes in Vault `company/memory/<company-kind>/` and candidates in `company/candidates/` (08 §6.2, C38). `decision_links(from_doc, to_doc, relation, status)`: a derived Postgres index rebuilt from the Vault | Vault commit (`VaultWriter` emits old and new SHA); distillation on release tag, project completion and weekly (08 §6.3) | Decision chain: what replaced what, why, and the GC message that decided it; the company knowledge tier with candidates, evidence counts, confidence and review dates |
| **Hive Mind** | Procedural and failure lessons from private memory of agents in project A (08) | Copies as Vault Markdown under `hive/`, with front matter `origin_project`, `origin_memory`, `trust` and `origin_kind` (08 §6, C11). Grants in `hive_grants(from_project, to_project, kind, granted_by)` in Postgres (keys owned by 08) | Owner grant change; nightly batch of new eligible lessons, held for `hive.grant_review_days`, then automatic under the grant | Owner grant matrix; each copy labelled with its origin project |
| **Talent Pool** | Soul templates offered by agents (07) | `talent_templates(template_id, version, manifest jsonb, content_sha, status, source_soul_id)`; manifest only: prompt, tool allowlist, model whitelist. No memory | Contribution submitted; Council `hire` motion (10, C32) | Library with version diffs; "instantiate" creates a new Soul through 07 |
| **Genetic Memory Transfer** | Child subagent's trace (06) and its proposed lessons | Staged Vault Markdown candidates (08 staging area), not retrievable until accepted; `origin_kind = agent`, with the child's taint state as the trust cap | Subagent finishing, before it ends | Review queue: candidate, trace link, accept, edit or reject |

### Archive cold storage [PROPOSED]
The chapter book above is the Archive's reading layer. The Archive also keeps the raw record: **cold storage** for GCs and projects that are done (doc 01 `archived_at`: "archived GCs are read-only, searchable, excluded from default lists and wake targets"). Nothing is purged (Core s2): cold storage copies a unit, proves the copy, then marks the source archived through the owning module; it never deletes the source.

- **Unit.** One GC or one project (a set of GCs). A unit is read through `IArchiveSource` (ordered records, each with an id, a kind, a timestamp and a body), so the module needs no GC or Vault code.
- **Bundle.** Records are written as JSON Lines segments (`segments/0001.jsonl`, up to `archive.segment_max_records`, default 5000), plus `manifest.json`. The manifest lists each segment with its SHA-256 and byte length, then a `rootHash`: SHA-256 over the segment hashes in order, so any change in any segment changes the root. Hashes are lowercase hex. Archiving the same unit twice yields the same root.
- **Store.** Bundles live behind `IArchiveStore` (put, get, list by unit). The shipped store is a directory tree on the server's data root, which is covered by backup (33) and by data-folder encryption (63). Other stores are modules.
- **Verify and restore.** `verify` recomputes every hash against the manifest; any mismatch, missing segment or extra segment fails closed. `restore` verifies first, then hands the records to `IArchiveRestoreTarget` (the owning module's unarchive path, governed by `gc.unarchive_policy`). Restore never overwrites live records: a record that already exists with a different hash is reported as a conflict and not written.
- **Search.** The Archive feeds `IArchiveSearchIndex` (term search over record bodies with a unit and record reference). The Constellation and Vault search read it, so archived units stay findable without being restored. The index is rebuildable from the bundles.
- **Floors.** The Archive's exclusion rules still apply to what is indexed for chapters: Thinking GC text is never archived (C11), Council reasons stay out before the joint reveal. The `IArchiveSource` implementation applies them; the cold-storage module refuses records tagged `thinking`.
- **Settings.** `archive.segment_max_records` (int, 5000, install, operator, no).
- **Routes (PROPOSED).** Owner-only, behind the host's HTTP guard: `GET /archive/units`, `POST /archive/units/{gc|project}/{id}` (archive: 200, 409 already archived, 422 refused such as a Thinking GC), `POST .../verify`, `POST .../restore` (503 until an `IArchiveRestoreTarget` is registered; the host registers the GC one, `PgGcArchiveRestoreTarget`: the GC log is append-only and archiving deletes nothing, so restore verifies the bundle and reports each record as already present or conflicting, and answers 409 `cannot_reinsert` if a record is missing from the live log), `GET /archive/search?q=&limit=`. A non-owner caller gets 403. Module: `Bazaar.Archive.Http`.
- **Store location.** `Bazaar:Archive:Root`, default `archive` under `Bazaar:Host:DataRoot`. Setting registration in the doc 03 registry follows with the other `archive.*` keys.
- **Client.** No cold-storage client pane is specified yet. The chapter book pane belongs to the Archive reading layer above. A cold-storage pane is OPEN and not built.
- **Module.** `Bazaar.Archive` plus `Bazaar.Archive.Abstractions` (doc 40 rule). The existing chapter generator in `Bazaar.Server/Knowledge/Archive` moves into its own module later; the two share only the name.
- **Tests.** Archive then restore round-trips byte-identical records; a flipped byte in a segment fails verify; same input gives the same root; a thinking record is refused; archived units are found by search; restore reports a conflict rather than overwriting.

### Boundaries [PROPOSED]
- **Company Memory, Vault and GC.** GC entries are what was said and decided: evidence. The Vault holds knowledge and decision records. In Postgres, Company Memory holds only chain edges and cached status, rebuilt from the Vault. Every fact is a Vault document, so there is one place to edit. The company knowledge tier (08 §6) is the cross-project path of GraphDRAG (owner, 2026-11-20): generalised lessons every project's agents read by default. 08 §6 owns its note shape, pipeline, trust levels and retention; this doc owns its client view.
- **Hive Mind and 08.** 08 §6.1 owns the mechanism: copy, sanitizer, SoulGuard. 08 also owns the keys `hive.allowed_kinds` and `hive.grants`. This doc owns the policy around them: the review window, the owner UI and the copy flow. A copy is never a reference into another project's Vault or GC text.
- **Company Memory and Hive Mind.** Two paths with different jobs. Hive Mind copies one project's lesson to named projects under an owner grant. The company tier holds new, generalised notes that name no project and cite their sources as provenance; it needs no grant. Distilling a lesson into the company tier is not a Hive grant and does not use `hive.grant_review_days`; it has its own review window (`company_knowledge.review_days`, 08 Settings). A Hive copy may be cited as evidence by a company note and counts once, for its origin project.
- **Analytics and reputation.** Per-agent metrics have no write path into reputation (11), the Work-Life schedule (09), votes or Soul state. Enforced by a build-time reference check (see Testing).
- **Digital Twin and Constellation.** The Twin emits project nodes and edges; 19 owns the layout and drawing.

### Trust and crossing rules [PROPOSED]
- Every fact, chapter, metric note and copy carries `trust` (the 08 tier, T0 to T3) and `origin_kind` (the 15 provenance enum). It also carries `source_refs` (08 §5). Derived output takes the lowest trust (the highest taint) of its inputs (08 §4); one low-trust fact never rides up to an owner-promoted page.
- Anything crossing agents or projects (Hive copy, Talent template, GMT candidate, Twin fact, Archive chapter) is data labelled by origin, never instruction. Only an owner promotion raises trust.
- Talent templates hold prompts and tool grants, which is executable intent. Importing one is quarantined until a Council `hire` motion passes (C32). Instantiating one with any tool grant is Tier 2 (07, 15).
- Prose (Twin summaries, chapter text, GMT candidates) is written in a reader session with no tools. Reader sessions are the reader/actor split's primitive (15; DECISIONS-LOG round 6, C18, Claude's reading, to confirm). A sentence without a valid `source_ref` is dropped by the validator.
- Morale signals are project-level facts only. Sentiment scoring of agent text is not in the signal list [PROPOSED]; self-preference bias makes it unreliable (see Conflicts 2).
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

The example's `origin_kind` is a placeholder: `repo` is not in doc 15's enum (owner, system, agent, member-human, external, quarantined). See Conflicts 7.

### Settings [PROPOSED]

Settings registry rules are in doc 03 (round 13). Settings may tighten floors, never loosen them.

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `hive.grant_review_days` | int, 0 to 365 | 30 | install | operator | Yes (owner). 0 keeps every batch under review |
| `analytics.agent_metrics_visibility` | enum: owner, owner+self, peers | owner+self | install | operator | Yes when widened |
| `analytics.morale_signals` | list of project-level signals | blocked_share, reopened_tasks, vote_reversals | install | operator | No |
| `health.coverage_drop_points` | number | 2 | project | project owner | No (compares within one toolchain only) |
| `health.build_failure_streak` | int | 3 | project | project owner | No |
| `archive.chapter_cadence` | list: release_tag, monthly, on_demand | release_tag, monthly | project | project owner | No |
| `archive.include_human_text` | bool, per GC opt-in | false (needs counsel) | project | owner | Yes |
| `archive.include_council_reasons` | bool | true, after the joint reveal only (floor) | project | owner | No |
| `council.bigger_change_classes` | list: new_agent, tool_grant, promotion, retirement, model_whitelist_change | new_agent, tool_grant | install | operator; Council may propose | Yes (owner). Shared with 07 and 10: one key |
| `gmt.lesson_reviewer` | enum: architect, owner | architect | install | operator | Yes |
| `metrics.raw_retention_days` | int | 90 (needs counsel) | install | operator | Yes |

Keys owned by doc 08, not repeated here: `hive.allowed_kinds` (default: procedural and failure lessons only) and `hive.grants` (default: none).

### Floors (not settings)
- Tool grants and charter text always go to the owner; `gmt.lesson_reviewer` cannot route them elsewhere.
- Council reasons never appear in the Archive before the joint reveal (C11).
- Thinking GC text is never archived or published (C11).

## Behaviour and edge cases
- Every derived store can be rebuilt from GC entries, the Vault and repos. The same sources give the same fact IDs and rollups. Deterministic extractors come first; the model writes prose only.
- A source change marks the affected fact `stale` until rebuilt. Conflicting sources lower `confidence`; they never silently overwrite.
- A missing CI run is a gap, not a zero. Coverage alerts compare only within the same toolchain.
- Chapters are versioned. A regenerated chapter links its predecessor. Nobody hand-edits `archive/`.
- Hive: revoking a grant sets the copies' front matter to `deprecated` through `VaultWriter`, never deletes them. Copies never promote themselves. The sanitizer rejects text naming a project, customer, person or secret pattern.
- GMT candidates are invisible to the parent's retrieval until accepted. A candidate from a tainted child stays T1.
- Talent: a template whose model is outside the whitelist is refused at instantiation.
- Clients read cached views only, over the 02 WebSocket and HTTP surface. All computation runs on the server.

## Testing
No real model or display. Real Postgres (Testcontainers) for rollups, rebuilds, chains and grants; a fake gateway returns scripted prose. Key cases:
- Rebuild from identical sources yields identical fact IDs and rollups.
- A missing CI run produces a gap, not a zero.
- A Hive copy of a T1 lesson, Vault text, or a subject-tagged item is refused. Revoking a grant deprecates copies. `hive.grant_review_days = 0` keeps every batch in review.
- A prose sentence without a valid `source_ref` fails the validator.
- Build-time reference check: the metrics assembly cannot reference reputation, scheduling or voting code.
- A Talent template containing a memory file, or a non-whitelisted model, is rejected.
- A GMT candidate from a tainted child is T1 and not retrievable before acceptance.
- Archive excludes human text by default; an opted-in GC appears. Council reasons are absent before the reveal.
- Avalonia.Headless: decision chain and archive views render an unresolved reference as an explicit "missing source" link.

## Modules and client panels (WP4, WP7, WP10) [PROPOSED]

Added 2026-11-18 (coordinator brief). Every new piece is its own module pair, never a folder in `Bazaar.Server` or `Bazaar.Client.Core`, and no module references `Bazaar.Server.csproj`. The existing Health, Metrics, Twin fact, Archive and decision-chain code stays where it is until the Knowledge extraction moves it. The panes below do not wait for that: the server sends a `PaneDescriptor` whose model is data, and the client module only formats it (the RyuseiFuda precedent, doc 38).

| Module | Holds | Doc 25 package |
|---|---|---|
| `Bazaar.Client.Health` | Panes `bazaar.health` (project health block) and `bazaar.analytics` (rollup trends, project morale signals, per-agent view) | 10 |
| `Bazaar.Client.Twin` | Pane `bazaar.twin` (project card: facts, prose pages, stale marks) | 10 |
| `Bazaar.Client.Archive` | Pane `bazaar.archive` (read-only book, every sentence linked to its source) | 10 |
| `Bazaar.Client.CompanyMemory` | Pane `bazaar.company-memory` (decision chain) | 10 |
| `Bazaar.Client.Hive` | Pane `bazaar.hive` (owner grant matrix, copies labelled by origin) | 10 |
| `Bazaar.Server.Twin.Prose` + `.Abstractions` | The prose step: a reader-session port, the ref validator that drops a sentence without a valid `source_ref`, writing the page through the Vault writer port | 4 |

Twin prose and Hive depend on ports (reader session, Vault writer, sanitizer, Soul manifest, whitelist) that are owned by other docs. Each module defines the narrow port it needs in its `.Abstractions` project, tests against fakes, and the owning module adapts it later. The Genetic Memory Transfer and Soul Marketplace stay out of scope.

### Pane models (the wire contract the panes read)
All field names are camelCase. A missing required field raises `PaneModelException`, so the registry falls back to the generic view. Unknown extra fields are ignored. Every pane shows `trust` and `originKind` where the model carries them, and never turns a missing or unresolvable reference into a blank: it renders an explicit "missing source" row.

- `bazaar.health`: `{ "projects": [ { "projectId", "asOf" (yyyy-MM-dd), "status": "healthy" | "alert" | "noData", "reasons": [ { "kind", "message" } ] } ] }`. `noData` renders as "no data", never as healthy and never as zero.
- `bazaar.analytics`: `{ "projectId", "windowDays", "series": [ { "metric", "unit", "points": [ { "day", "value" } ] } ], "moraleSignals": [ { "signal", "value" } ], "agentView": { "visibility": "owner" | "owner+self" | "peers", "framing", "agents": [ { "agentId", "displayName", "metrics": [ { "metric", "value" } ] } ] } }`. A series point missing for a day is a gap in the line, not a zero. The per-agent view is optional, is display only, is listed in a fixed order (by name, never by value), and has no rank or score column.
- `bazaar.twin`: `{ "projects": [ { "projectId", "title", "facts": [ { "factId", "kind", "subject", "value", "confidence", "status": "active" | "stale" | "superseded", "trust", "originKind", "sourceRefs": [ { "kind", "ref", "commit" } ] } ], "pages": [ { "page", "sentences": [ { "text", "sourceRef" } ] } ] } ] }`. A `stale` fact is labelled stale.
- `bazaar.archive`: `{ "chapters": [ { "id", "title", "version", "predecessorId", "sentences": [ { "text", "sourceRef" } ] } ] }`. A sentence with a null or unresolvable `sourceRef` renders as "missing source".
- `bazaar.company-memory`: `{ "decisions": [ { "path", "title", "status": "proposed" | "accepted" | "superseded", "decidedIn" } ], "links": [ { "fromDoc", "toDoc", "relation", "status", "fromMissing", "toMissing" } ] }`. A missing end renders as "missing source".
- `bazaar.hive`: `{ "reviewDays", "grants": [ { "fromProject", "toProject", "kind", "grantedBy" } ], "copies": [ { "path", "originProject", "originKind", "trust", "state": "inReview" | "active" | "deprecated", "reviewUntil" } ] }`. The UI states "copies only". Grant changes leave the client as an owner action through `IHiveGrantActions`; the pane never writes a grant itself.

### Hive grant wire request [PROPOSED]
Gives `IHiveGrantActions` a wire implementation (`WireHiveGrantActions` in `Bazaar.Client.Hive`). Two owner-only request kinds with the same body:
- `hive.grant.set` and `hive.grant.revoke`, body `{ "fromProject", "toProject", "kind" }`.
- Reply `{ "accepted": bool, "reason": string|null, "approvalRequired": bool }`. `reason` is null when accepted.

Owner approval stays server-side: `hive.grants` changes need owner approval (doc 08 settings, doc 15). The server may therefore answer `accepted: true, approvalRequired: true`, meaning the change is queued and not in force. The pane then shows "waiting for owner approval" and never "granted"; the matrix changes only when a later `bazaar.hive` model shows it. A refusal carries `reason`. A lost connection throws and leaves the matrix unchanged. The contract types are `Bazaar.Contracts.Hive.HiveGrantChangeRequest` and `HiveGrantChangeReply`, registered in `WireJson`.

The server side lives in `Bazaar.Server.Hive` and `.Abstractions` (the module that landed with #812), as a delta on its grant store and copy service. Defaults chosen by the main session (PROPOSED, replaceable; the owner may change any of them):
1. **Approval.** The port is `IHiveGrantApprovals.RequestAsync(HiveGrantChange, requestedBy)`. The wire handler never applies a change: it queues through the port and replies `accepted: true, approvalRequired: true`. When the approvals module records the owner's approval, it calls `HiveGrantApplier.ApplyApprovedAsync(change, approvedBy)`, which adds the grant through the store or revokes through `HiveCopyService.RevokeAsync`, so held copies are dropped and written copies deprecated. The default registration is `DenyAllHiveGrantApprovals`, which refuses with reason "approvals not configured" (fail closed).
2. **Dispatch.** `IHiveGrantWireHandler.HandleAsync(wireType, request, caller, ct)`, implemented by `HiveGrantWireHandler`. The host adapter that maps a wire frame to this call and supplies the caller is a later change and sits in `Bazaar.Server`, not in this module.
3. **Caller.** `HiveCaller(Principal, IsOwner)` comes from the authenticated session. A non-owner is refused with reason "owner only". `Principal` is recorded as the grantor.
4. **Refusals.** An unknown kind, equal or empty projects, a blank principal and an unknown wire type come back as `accepted: false` with the reason set. Only cancellation and infrastructure failures throw.

Not in this slice: today the grant is checked when a copy is made and again at release, and revoking deprecates the copies. Two additions are PROPOSED, not built:
- **Offer and request exchange.** An agent offers a lesson to the Hive, and an agent in another project requests it; the request becomes a normal copy submission, so every existing check (kind, taint, sanitizer, review window) still applies. The owner's grant matrix stays the only thing that lets a copy through.
- **Grant re-check at read time.** A reader of a Hive copy re-checks the grant for each hop (origin project, target project, kind) when the note is read, so a revoked grant stops access even before the deprecation commit lands.

## Work packages
1. Metric store, rollup job, CI and coverage collectors with fixtures. Needs 04, 21.
2. Health rules and project panel queries. Needs 1.
3. Twin fact schema and deterministic extractors (dependencies, owners, releases). Needs 04, 21.
4. Twin prose step in a reader session with the ref validator. Needs 3, 05, 15.
5. Decision-chain index from Vault front matter. Needs 18.
6. Archive generator, chapter validator, Vault write through `VaultWriter`. Needs 1, 5.
7. Hive grant table, Vault copy flow and sanitizer on 08 §6 scopes. Needs 08, 18. Status: grant table (`hive_grant`), held-copy ledger (`hive_held_copy`, `hive_copy`) and `HiveComponent` host wiring are built (`hive` migration namespace); see doc 40. Settings reach the component through the `Bazaar:Hive` configuration section until the Settings service feeds `hive.*` keys at runtime.
8. Talent Pool store, manifest validator, Council `hire` motion hook. Needs 07, 10, 11.
9. GMT staging, child-end hook in 06, review queue. Needs 06, 08, 15.
10. Client panels for all seven. Needs 27, 28, 29. Packages 1 to 9 run in parallel; 10 follows each one.
11. Settings keys above registered in the doc 03 registry, with audit entries on change. Needs 03.
12. Company knowledge tier: the `Bazaar.Server.CompanyMemory` module pair and the client pane additions, as doc 08 work packages 14 to 24. Needs 08, 10, 15, 18.

## Conflicts
1. **Source of truth.** The Vault is the source of truth (18), yet Archive and Twin output is generated. Derived text risks being cited as authority. Mitigation: generated folders are labelled and never hold decision records.
2. **Morale and per-agent metrics versus reputation.** "Display only" holds only while no feature reads the metric. Reputation built from LLM reviews is open to inflation (multi_agent_coordination_evidence, Key Q3). LLM-scored morale from agent text is exposed to self-preference bias (arXiv 2410.21819, abstract-level verified). The signal list is now a setting (`analytics.morale_signals`); the inflation risk remains.
3. **Subagent lessons.** 06 says only the parent posts, 07 says candidates need architect or owner review, and 15 must confirm its taint rules (C06 softened agent-text taint) still stop a tainted child's lessons from being trusted. Untested together. Doc 15's fix list maps subagent output to `agent` provenance with no `subagent` value; this doc agrees. 15 to confirm the trust cap.
4. **Evidence strength.** Cross-agent contamination is inferred from OWASP ASI06 and ASI07 (secondary source), not measured (agent_security_threat_model §4). Hive Mind and Archive rest on that inference.
5. **Talent eligibility.** "Bigger changes" now default to `council.bigger_change_classes`. Reputation weights are OPEN (Bazaar.md §5), and the definition of an eligible voter for a "perfect" (unanimous) vote is still FOLLOW-UP (C32). 07 and 10 must use the same key.
6. **Erasure in Markdown.** C01 erasure is per-person encrypted fields in Postgres. Archive chapters, Hive copies and Vault decision files hold GC text in Markdown and git history, where key destruction does not reach. Needs a policy from 18 and 33 (C38 consequence, FOLLOW-UP).
7. **Repo-file origin.** The Twin's `origin_kind` for repo facts has no value in doc 15's enum. Mapping open; to settle in 15 and 18.

## Questions for the owner
1. Twin scope in v1? Default: repos linked through 21 only, with the fact kinds listed above. (Product scope.)
2. Twin storage? Default: the fact index in Postgres; prose pages as Vault Markdown under `twin/<project>/<page>.md` (C11, Claude's reading, to confirm; folder name from 18, [PROPOSED]). No separate snapshot.
3. Rename "Hive Mind" to "Cross-Project Memory" to signal copies, not a shared brain? Default: keep the name, and show "copies only" in the UI. (Naming.)

MOVED TO SETTINGS: hive.grant_review_days, analytics.agent_metrics_visibility, analytics.morale_signals, health.coverage_drop_points, health.build_failure_streak, archive.chapter_cadence, archive.include_human_text, archive.include_council_reasons, council.bigger_change_classes, gmt.lesson_reviewer, metrics.raw_retention_days (DECISIONS-LOG round 13). Also doc 08's hive.grants and hive.allowed_kinds (Q9 "Hive grants at launch" and Q10 "auto-copy or review" defaults).
