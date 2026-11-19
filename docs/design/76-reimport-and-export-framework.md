# 76. Reimport, Export Framework, Archive Sync and Narrative

Status: PROPOSED (2026-11-19). The owner's direction of 2026-11-19 to adopt the Resibo persistence features as Bazaar modules is DECIDED, and where a Resibo feature conflicts with Bazaar's design it becomes a separate module instead of a choice. Every design detail below that direction is PROPOSED or OPEN. Nothing is built; no code, migration or pane exists yet. Module ids and project names are working names pending owner naming.
Depends on: 01-gc-data-model.md (append-only log, versions), 18-vault.md (Markdown in git, `VaultWriter`, conflict cards), 33-backup-dr-and-export.md (backup targets, restore, drills), 38-ryusei-fuda.md (board import), 40-core-feature-modules-audit.md (module layout), 44-data-portability.md (importers, exporters, `import_item`, bundle), 46-kodo-health-module.md (health contract, restore drill), BAZAAR-COMPONENTS.md (manifests, migrations, `Principal`, module sides).
Used by: 44 (re-import and export registry behind its pipeline), 33 (offsite target set, restore-drill inputs), 46 (health and drill inputs), 38 (board re-import), 18 (import conflicts into the Vault).

## 1. Purpose

Resibo (the sister receipt program) has four persistence features that Bazaar lacks or only sketches: a three-way re-import that never silently overrides a conflict, a provider-registry export framework, an archive sync that mirrors to several targets, and a disaster-recovery restore with a verification report. Resibo's Historian also keeps a human-readable narrative track apart from its raw change log. The owner asked (2026-11-19) for everything in Resibo that can benefit Bazaar to be brought in, overengineered, with conflicts split into separate modules.

This document designs the result:

- `Bazaar.Server.Reimport`: idempotent re-import with a three-way diff (base, theirs, ours). Conflicts go to a human.
- `Bazaar.Server.ExportFramework`: a registry of export providers. Each format is its own provider module.
- `Bazaar.Client.Reimport`: the diff review pane.
- `Bazaar.Server.ArchiveSync`: an optional composite offsite target (parallel multi-target backup, cursor, three-way-diff sync) for doc 33's provider set.
- `Bazaar.Server.Narrative`: an optional displayable "why did this change" track, kept separate from the raw log.
- Restore-drill planning inputs for Kodo, taken from Resibo's disaster-recovery design.

All of it respects the GC rule: re-import never rewrites GC history (section 11).

## 2. Decided inputs

- Owner, 2026-11-19: incorporate everything from Resibo that benefits Bazaar; overengineer; a conflict with Bazaar's design becomes a separate module, not a choice. DECIDED.
- Owner, 2026-11-13 (doc 44 D1 to D4): everything is exportable; importers and exporters are modules; re-importing the same source is a no-op, not a duplicate. DECIDED.
- GCs are append-only and never purged; edits keep every version with an "edited" marker (doc 01, owner 2026-10-24). DECIDED.
- Vault is Markdown in git; one `VaultWriter` per repo owns every write; `*.md merge=text`, never `union` (doc 18).
- Backup target is the operator's choice, documented, with hooks (doc 33, 2026-10-25).
- Every feature is its own module `Bazaar.Server.<Name>` plus `.Abstractions`, with a `Bazaar.Client.<Name>` module when it has a client side. No feature code in `Bazaar.Server` or `Bazaar.Client.Core` (BAZAAR-COMPONENTS 2.1; CLAUDE.md).
- `Principal` (`kind:id`, kinds `agent`, `user`, `service`, `system`) is the type for who acted (BAZAAR-COMPONENTS 4.2, 2026-11-19). DECIDED.
- Providers are plain modules on Core, with no selector (owner, 2026-11-19, relayed). Shared libraries and modules hold contracts and logic only, with no third-party dependencies; providers are separate modules that may carry them.
- Configurability rule (DECISIONS-LOG round 13): open values are operator settings with safe defaults; DECIDED items and security invariants are not settings.

## 3. What this doc does not decide

- Doc 44's formats, bundle layout, erasure interaction and named importers. This doc adds to its pipeline; it does not restate it.
- Doc 33's backup engines, key custody, rollback policy and restore wizard. This doc adds a target decorator and drill inputs.
- Which accounting or regulatory exports Bazaar ships (OPEN, section 13).
- The general module contract (doc 42) and the audit log storage (doc 48).
- Whether narrative text is ever model-generated (OPEN, section 8.4).

## 4. What exists today and what is reused

Read before designing: docs 40, 44, 33, 18, 46, `BAZAAR-COMPONENTS.md`, the DECISIONS-LOG, and `src`.

| Existing piece | Where | How this doc uses it |
|---|---|---|
| `IPortabilityImporter`, `IPortabilityExporter`, `SourceRecord`, `SourceKeys`, `ImportPlanner`, `ImportPreview` | `src/Bazaar.Server.Portability/Contracts.cs` | Reused as is. Reimport consumes `SourceRecord` and its `SourceKey`. It does not define a second record type. |
| `import_item` table (doc 44 8.4: importer, source key, content hash, target kind and id, target version, job) | planned under doc 04 | Reused as the identity table. Reimport reads it to find the target for a key and adds its own snapshot tables (section 6.2). |
| Doc 44 idempotent rule: same key and same hash is a no-op; same key and changed content is a new version | doc 44 8.4 | Kept for the no-conflict case. Reimport adds the case doc 44 leaves open: the target was also changed on our side since the last import. |
| `ImportCommitter`, `VaultImportTarget`, `GcImportTarget`, `ImportAudit` | `Bazaar.Server.Portability` | Reimport produces a resolved plan and hands writes to these committers. It never writes a target itself. |
| `ExportBundle`, `BundleSigner`, `ExportBundleCipher` | `Bazaar.Server.Portability` | Reused unchanged. The framework produces provider output. The bundle writer wraps it. |
| `IBackupTarget` (`PutDirectoryAsync`, `ListAsync`), `ResticBackupTarget`, `OffsiteRetentionPolicy` | `Bazaar.Contracts/Backup`, `Bazaar.Server.Components.Backup` | `ArchiveSync` implements `IBackupTarget` as a composite over several targets. The restic target stays one provider. |
| `RestoreRunner`, `RestoreWizard`, `RestoreAuditVerifier`, `BackupSetDigest`, `RetentionPlanner` | `Bazaar.Server.Components.Backup` | Reused. Section 9 adds only verification and planning inputs. |
| `IModuleHealthContract`, `ModuleHealthCheck` | `Bazaar.Server.Modules.Abstractions` | Each new module implements it. |
| `ITransactionalAuditLog`, `IAuditLog` | `Bazaar.Audit.Abstractions` | All audit events below go through it, with `Principal`. |
| Vault conflict card (doc 18 point 5, `vault.conflict_card_actions`) | doc 18 | Reused as the Vault-target conflict surface. Reimport mirrors the conflict into its own list so one pane shows all conflicts. |
| `Bazaar.Client.Portability` and `.Avalonia` | `src` | Import preview lives there. The review pane is a separate module (section 7). Nothing is duplicated. |
| Pane registry (`IPaneModule`, `PaneRegistry.RegisterView`) | `Bazaar.Client.Core/Panes` | The pane registers through it. |

Nothing named three-way, `DiffViewer`, an export-provider registry, or a narrative track exists in `src` (grep, 2026-11-19). The Jobs seam referenced in doc 40 (`IJobHandler`) runs long exports and syncs. Its abstractions project was not found under a `Bazaar.Server.Jobs*` project in `src`, so the exact seam is OPEN (section 13).

Overlaps found:

1. Doc 44 section 8.4 already defines re-import by `source_key`. Reimport extends that section with base and ours; it does not replace it.
2. Doc 44 section 9 sketches `IExporter` as `IBazaarModule` returning streamed `ExportFile`s, while `src` has `IPortabilityExporter.Export(records) -> byte[]`. The framework registry defines one provider contract (section 5.3) and doc 44's exporters register through an adapter. Which of the two sketches wins is a call for the doc 44 owner (OPEN).
3. Doc 44 8.10 (replace choice) is cited by `ImportPreview.ReplaceSourceKeys` in code, but the copy of doc 44 read for this doc ends at 8.9. Reimport must honour that choice. The section needs to be located or written (OPEN).
4. Doc 18 resolves Vault conflicts with a conflict card and an agent rebase. Reimport conflicts for Vault targets reuse that. See section 6.5.

## 5. Design: Bazaar.Server.ExportFramework

### 5.1 Modules

| Project | Role |
|---|---|
| `Bazaar.Server.ExportFramework.Abstractions` | `IExportProvider`, `IExportRegistry`, `ExportRequest`, `ExportResult`, `ExportScope`, the round-trip token types. Contracts only, no third-party dependencies. |
| `Bazaar.Server.ExportFramework` | Registry, run orchestration, scope check, outbound archive-bomb check, run records, audit. Component id `export-framework`, settings section `Bazaar:ExportFramework`. Hard `dependsOn`: `audit`. Soft: `portability`, `reimport`, `narrative`, `kodo`. |
| `Bazaar.Server.Export.<Format>` | One provider module per format, each with its own manifest (`kind: provider`, `provides: export:<format-id>`). Providers may carry third-party dependencies (a spreadsheet writer, for example). |

There is no selector. Every enabled provider module is in the registry, and the caller names the provider id, as Resibo's export registry does (deep-dive 31 section 2: a registry of which providers are registered, running whichever one was asked for). Adding a format never touches registry code.

### 5.2 Provider set

| Provider module (working id) | Output | Round trip | Source |
|---|---|---|---|
| `Bazaar.Server.Export.Jsonl` (`jsonl.v1`) | GC, Kanban and usage records as doc 44 section 4 | No (evidence) | doc 44 |
| `Bazaar.Server.Export.Csv` (`csv.table`) | Flat tables (Kanban tasks, usage, reputation) | Yes, with sidecar token | doc 44 4.4; Resibo `excel_general` idea |
| `Bazaar.Server.Export.Xlsx` (`xlsx.table`) | Raw-data sheet plus formula-driven summary sheet (Resibo technique), hidden token cell | Yes | Resibo `excel_general` |
| `Bazaar.Server.Export.Markdown` (`md.tree`) | Vault tree or transcript, front matter token | Yes (Vault three-way) | doc 44 4.3 |
| `Bazaar.Server.Export.GraphMl` (`graphml`) | Constellation view | No | doc 44 4.5 |
| `Bazaar.Server.Export.AuditPackage` (`audit.package`) | Zip: referenced blobs, filtered audit and journal export, summary sheet | No | Resibo `audit_package`; doc 44 4.6 |
| `Bazaar.Server.Export.Portability` (`portability.bundle`) | Whole-company or per-subject bundle | No | Resibo `data_portability`; doc 44 4.z |
| `Bazaar.Server.Export.ScopedSet` (`scoped.set`) | All items for a named scope (a project, a GC set, a team) with a contributor column showing display name, never a raw id | Yes | Resibo `group_export` |

The Resibo accounting and regulatory providers (`slsp_summary`, `quickbooks_export`, `xero_export`) are receipt-domain. They are not adopted. Section 13 asks whether the owner wants a Bazaar cost or usage export for accounting tools; if so each is a provider module on this registry.

### 5.3 Contracts

```csharp
namespace Bazaar.Server.ExportFramework;

public sealed record ExportRequest(
    string ProviderId, Principal Requester, ExportScope Scope,
    IReadOnlyDictionary<string, string> Parameters, bool IncludeRoundTripToken);

public sealed record ExportScope(string Kind, string? ScopeId);   // "instance", "gc", "project", "subject"

public sealed record ExportFileRef(string Path, string MediaType, long Bytes, string Sha256);

public sealed record ExportResult(
    bool Ok, string RunId, string ProviderId, string FormatVersion,
    IReadOnlyList<ExportFileRef> Files, string? RoundTripToken,
    string? ErrorCode, string? ErrorDetail);                         // errors are data, as in Resibo

public interface IExportProvider
{
    string Id { get; }                       // "xlsx.table"
    string FormatVersion { get; }
    bool SupportsRoundTrip { get; }
    ExportScopeKinds SupportedScopes { get; }
    IAsyncEnumerable<ExportChunk> GenerateAsync(ExportContext ctx, CancellationToken ct);
}

public interface IExportRegistry
{
    IReadOnlyList<ExportProviderInfo> List();
    ValueTask<ExportResult> RunAsync(ExportRequest request, CancellationToken ct);
}
```

Rules, from Resibo (deep-dive 31 section 2, `ExportProvider` protocol and error types) and adapted:

- A provider never keeps its own copy of data and never decides when it runs. A caller does (a user, a schedule, a data-subject tool).
- An unknown or failing provider is an `ExportResult` with an error code, never an exception across the module boundary. A provider that throws cannot break the caller.
- Scope follows the caller's rights (doc 44 4.1: the owner exports everything; other humans export their own; agents export nothing). The registry checks this before it calls a provider. A provider never receives a bare scope id it did not check.
- Long runs go through the jobs seam as background jobs. A user download of a small export runs immediately.
- Outbound archive check: every provider that writes a zip passes the same expansion-ratio, size and entry-count checks that doc 44 section 8.3 applies to inbound archives. This is Resibo's "symmetric bomb check" (`v3-deepdive-31`). Reason: a corrupt or hostile record set should not produce an export that is itself a bomb for the reader.
- Round-trip token: see section 6.3.

### 5.4 Data

Migration namespace `export-framework`, folder `src/Bazaar.Server.ExportFramework/Data/Sql/export-framework/`.

```sql
-- 0001_export_run.sql
CREATE TABLE export_run (
  run_id        uuid PRIMARY KEY,
  provider_id   text NOT NULL,
  format_ver    text NOT NULL,
  requester     text NOT NULL,            -- Principal text form, kind:id
  scope_kind    text NOT NULL,
  scope_id      text NULL,
  state         text NOT NULL CHECK (state IN ('queued','running','complete','failed','incomplete')),
  files         jsonb NOT NULL DEFAULT '[]',   -- [{path, sha256, bytes}]
  token_id      uuid NULL,
  started_at    timestamptz NOT NULL DEFAULT now(),
  finished_at   timestamptz NULL,
  error_code    text NULL
);
CREATE INDEX export_run_requester ON export_run (requester, started_at DESC);
```

Export files themselves live in the blob store behind `IBlobStore` (`Bazaar.Server.Blobs.Abstractions`), not in this table.

### 5.5 Settings

| Key | Type | Default | Floor | Change needs approval |
|---|---|---|---|---|
| `export.max_run_bytes` | int | 10 GiB | host storage | No; raising is audited |
| `export.retention_hours` | int | 24 | min 1 | No |
| `export.outbound_max_expansion_ratio` | int | 100 | min 10 | No |
| `export.round_trip_token_ttl_days` | int | 90 | min 1 | No |
| `export.allow_plaintext_personal` | bool | false | Owned by doc 44 `portability.export.allow_plaintext_personal`; this key only references it | Yes (owner) |

### 5.6 Audit events

`export.run.started`, `export.run.completed`, `export.run.failed`, `export.token.issued`. Each carries the requester `Principal`, provider id, scope, file hashes and byte totals. Doc 44's `portability.export` entry is written by the bundle provider and links the same run id.

### 5.7 Kodo health contract

Liveness: registry loaded. Readiness: at least one provider registered, blob store reachable. Dependencies: `audit`, blobs. Self-test: run the `jsonl.v1` provider against a built-in two-row fixture and verify its hash. Each provider module adds its own self-test with a built-in fixture (doc 44 section 9 rule).

## 6. Design: Bazaar.Server.Reimport

### 6.1 Modules

| Project | Role |
|---|---|
| `Bazaar.Server.Reimport.Abstractions` | `IThreeWayResolver`, `IReimportService`, `ISnapshotStore`, `FieldConflict`, `ResolutionOutcome`, `ReimportPlan`, `ReimportSession`, `IReimportTarget`. Contracts and the pure resolver only. No third-party dependencies. |
| `Bazaar.Server.Reimport` | Session service, snapshot store (Postgres), conflict store, target adapters for Kanban, config and settings, GC (append-only adapter), Vault (git adapter), audit. Component id `reimport`, settings `Bazaar:Reimport`. Hard `dependsOn`: `audit`, `portability`, `export-framework`. Soft: `gc`, `vault`, `ryusei`, `kodo`, `narrative`. |

### 6.2 The three sides and the algorithm

Terms for Bazaar, mapped from Resibo's (`v3-deepdive-30-reimport.md`):

- Base: the snapshot recorded when the object was last exported or imported (Resibo's `original`).
- Theirs: the incoming record from the new source (Resibo's `reimported`).
- Ours: the current Bazaar state (Resibo's `canonical`).

The pure function, per field, over the fields present in theirs only (a field the source does not carry is never treated as a request to clear it; this is Resibo's rule, kept):

| Theirs vs base | Ours vs base | Result |
|---|---|---|
| Unchanged | any | Ours stands. A stale source never reverts a newer change. |
| Changed | Unchanged | Apply theirs. |
| Changed | Changed, same value as theirs | Ours stands. No conflict, because there is nothing for a human to decide. |
| Changed | Changed, different value | Conflict. Ours stays in place and the conflict is recorded. Never resolved automatically. |

None of these four rules may be softened by a setting. There is no last-write-wins, and no rejection of a whole re-import over one conflicting field (Resibo `PRINCIPLES.md` section 4.3, "never silently override, always surface a genuine conflict to a human").

```csharp
namespace Bazaar.Server.Reimport;

public sealed record FieldConflict(
    string TargetKind, string TargetId, string Field,
    string? Base, string? Ours, string? Theirs);

public sealed record ResolutionOutcome(
    IReadOnlyDictionary<string, string?> Resolved,
    IReadOnlyList<FieldConflict> Conflicts,
    IReadOnlyList<string> AppliedFields);

public interface IThreeWayResolver
{
    // Pure. No I/O. Throws nothing for ordinary data.
    ResolutionOutcome Resolve(
        IReadOnlyDictionary<string, string?> @base,
        IReadOnlyDictionary<string, string?> ours,
        IReadOnlyDictionary<string, string?> theirs);
}

public interface IReimportService
{
    ValueTask<ReimportSession> SubmitAsync(ReimportSubmission submission, CancellationToken ct);
    ValueTask<ReimportSession> GetAsync(Guid sessionId, CancellationToken ct);
    ValueTask<ConflictDecisionResult> DecideAsync(Guid conflictId, ConflictDecision decision,
        Principal decidedBy, Principal? onBehalfOf, CancellationToken ct);
    ValueTask<ReimportSession> CommitAsync(Guid sessionId, Principal by, CancellationToken ct);
}

public enum ConflictChoice { TakeTheirs, KeepOurs, ManualValue }
```

Field values are normalised through a per-target projection before comparison, because an export is a lossier view than the stored object. This is Resibo's field-mapping lesson (`core/persistence/reimport/CLAUDE.md`): without projecting both base and ours into the export's own representation, a pure round trip with no edit reported dates as user-changed. A theirs value that cannot be read as the field's type goes to `rejected_fields`, reported and never written as is or silently dropped.

### 6.3 Snapshot base and round-trip token

Resibo embeds a hidden cell (Historian event id plus time) in the workbook and uses it as the base. Bazaar keeps that idea with two changes:

1. The token is only a reference. The base values live in `reimport_snapshot` on the server, so a hand-edited file cannot forge its own base.
2. The token is signed with the instance export key held by the Identity Service (doc 44 6.3), so it cannot be swapped or invented. Verification failure rejects the re-import with an actionable message.

Each round-trip provider embeds the token in the way its format allows: a hidden cell in `xlsx.table`, a sidecar `.bazaar-token` file next to a CSV, a `bazaar_snapshot` front-matter key in Markdown, and a manifest field in JSON. A source with a missing, expired or unverifiable token is rejected with an error asking for a fresh export. It is never treated as "overwrite everything with ours" (Resibo deep-dive 30, missing snapshot-reference test; kept).

Sources that never had an export (a first Trello import, a fresh Obsidian vault) have no base. For those, the first import creates the snapshot, and later imports of the same source have a base. A re-import from a source with no snapshot at all runs as a two-way compare with every difference shown as a conflict if ours has changed since the object was created. This is OPEN for owner confirmation (section 13).

```sql
-- namespace: reimport, src/Bazaar.Server.Reimport/Data/Sql/reimport/
-- 0001_snapshot.sql
CREATE TABLE reimport_snapshot (
  snapshot_id   uuid PRIMARY KEY,
  token_id      uuid UNIQUE NULL,           -- export round-trip token
  source_kind   text NOT NULL,              -- 'export-run' | 'import-job'
  source_ref    text NOT NULL,              -- export_run.run_id or import job id
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    text NOT NULL,              -- Principal
  expires_at    timestamptz NULL
);
CREATE TABLE reimport_snapshot_item (
  snapshot_id   uuid NOT NULL REFERENCES reimport_snapshot,
  target_kind   text NOT NULL,              -- 'kanban_task','vault_file','setting','gc_message'
  target_id     text NOT NULL,
  target_ver    text NOT NULL,              -- task version, commit sha, GC message version no
  fields        jsonb NOT NULL,             -- projected field map (the base)
  PRIMARY KEY (snapshot_id, target_kind, target_id)
);
```

```sql
-- 0002_session.sql
CREATE TABLE reimport_session (
  session_id    uuid PRIMARY KEY,
  snapshot_id   uuid NOT NULL REFERENCES reimport_snapshot,
  submitted_by  text NOT NULL,              -- Principal
  state         text NOT NULL CHECK (state IN
                  ('scanning','diffed','awaiting_review','committing','complete','abandoned','failed')),
  fields_applied int NOT NULL DEFAULT 0,
  conflicts_total int NOT NULL DEFAULT 0,
  conflicts_open  int NOT NULL DEFAULT 0,
  source_sha256 text NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now(),
  expires_at    timestamptz NOT NULL
);
CREATE TABLE reimport_conflict (
  conflict_id   uuid PRIMARY KEY,
  session_id    uuid NOT NULL REFERENCES reimport_session,
  target_kind   text NOT NULL,
  target_id     text NOT NULL,
  field         text NOT NULL,
  base_value    text NULL,
  ours_value    text NULL,                  -- ours as seen at diff time
  theirs_value  text NULL,
  group_key     text NULL,                  -- for grouped review, section 7
  state         text NOT NULL DEFAULT 'open' CHECK (state IN ('open','resolved','stale')),
  choice        text NULL,
  manual_value  text NULL,
  decided_by    text NULL,                  -- Principal
  on_behalf_of  text NULL,                  -- Principal
  decided_at    timestamptz NULL,
  UNIQUE (session_id, target_kind, target_id, field)
);
```

A session persists server side, so a closed pane or client loses nothing (Resibo `v3-deepdive-49`). `expires_at` is bounded by `reimport.session_ttl_days`.

Stale conflicts: if ours changes again between diff time and a decision, the stored `ours_value` no longer matches. The conflict becomes `stale`, the pane shows the new ours, and the human decides again. A decision is never applied against an ours the human did not see. (This is added for Bazaar. Resibo's single-user receipts make the race unlikely, but Bazaar objects are edited by many agents.)

### 6.4 Targets and the append-only rule

Each target kind implements `IReimportTarget`:

```csharp
public interface IReimportTarget
{
    string Kind { get; }
    ValueTask<IReadOnlyDictionary<string, string?>> ReadOursAsync(string targetId, CancellationToken ct);
    ValueTask<WriteResult> ApplyAsync(string targetId, IReadOnlyDictionary<string, string?> fields,
        Principal actor, ReimportAttribution attribution, CancellationToken ct);
    TargetWriteMode Mode { get; }     // InPlaceVersioned | AppendOnly | GitMerge
}
```

| Target | Mode | What a re-import may do |
|---|---|---|
| Kanban task (Workday, doc 12, 38) | InPlaceVersioned | Apply changed fields through the board service as a normal transition with attribution. Columns follow doc 12 gates; a gate refusal is a conflict-like result shown to the human, never forced. |
| Config and settings | InPlaceVersioned | Apply only through the settings registry with floor checks (doc 44 8.2). A change below a floor is rejected as data. Secrets are never applied. |
| Vault file | GitMerge | See 6.5. |
| GC message | AppendOnly | See section 11. |
| Constellation graph hints, derived views | none | Not re-imported (doc 44 8.2: graph hints are not stored). |

### 6.5 Vault targets: native three-way

Markdown in git already has a three-way merge, so Reimport does not reimplement text merging. For a Vault target the base is the commit recorded in `reimport_snapshot_item.target_ver`, theirs is the incoming file, ours is the head. Reimport asks `VaultWriter` (doc 18) to merge on a branch `import/<session>`: non-overlapping edits merge automatically (doc 18 point 5), and overlapping edits abort the merge and keep the branch. Doc 18 sends a conflict card to the task's GC. Reimport additionally records one `reimport_conflict` row per conflicting file (field = path, values = three blob hashes) so the diff pane lists Vault conflicts beside the others. Resolution goes back to the Vault through `VaultWriter` as a new commit by the deciding `Principal`. Doc 18's rule is unchanged: `merge=union` is never used for Markdown, and `vault.merge_approval_paths` (the Constitution) still needs owner approval. A re-import that touches such a path always waits for that approval, even when the merge is clean.

### 6.6 Content scanning and trust

A re-imported file is untrusted input like any upload. It runs through the doc 44 parser sandbox (8.3), the secret scanner (8.5) and carries trust `external` (doc 15). Resibo makes the same point (its re-import is scanned "like any other untrusted upload"). Being the owner's own file coming back earns no trust.

### 6.7 Service flow

```
submit (file or import job) -> verify token -> scan + sandbox parse -> project fields
 -> load snapshot base -> read ours -> resolve (pure) -> session 'diffed'
 -> if conflicts: 'awaiting_review' (human decides each)   else: commit
 -> commit: apply clean fields per target in batches -> audit -> verify -> 'complete'
```

Clean fields may commit before conflicts are resolved if `reimport.commit_clean_first` is true (default true), because the conflict set is independent per field. Conflicting fields keep ours until decided. Commit uses doc 44's batch and checkpoint rules (8.7).

### 6.8 Settings

| Key | Type | Default | Floor | Approval |
|---|---|---|---|---|
| `reimport.require_token` | bool | true | Always true for round-trip providers (cannot be disabled) | Not settable |
| `reimport.commit_clean_first` | bool | true | none | No |
| `reimport.session_ttl_days` | int | 30 | min 1 | No |
| `reimport.group_confirm_required` | bool | true | Always true: grouped decisions need one explicit confirmation of the group | Not settable |
| `reimport.auto_resolve` | enum `off` | `off` | The only value is `off`. Conflicts are never resolved automatically | Not settable |
| `reimport.allow_on_behalf_of` | bool | true | Owner or a role the owner grants; always attributed | Yes (owner) to disable the attribution requirement is not allowed |
| `reimport.max_conflicts_per_session` | int | 20000 | 100 | No |
| `reimport.vault_branch_retention_days` | int | 30 | min 7 | No |

### 6.9 Audit events (all carry `Principal`)

- `reimport.submitted` (submitter, snapshot, source hash, counts)
- `reimport.diffed` (fields applied, conflict count)
- `reimport.conflict.decided` (decider, on-behalf-of, choice, field, target; values by hash, not plaintext, when the target is personal)
- `reimport.commit.batch` (batch, item count, hash; same shape as `portability.import.batch`)
- `reimport.token.rejected` (reason)
- `reimport.session.abandoned`

Applied fields are attributed in the target's own history as `user:<id>` (or the agent's `Principal`) with a `via: reimport:<session>` attribution, the analogue of Resibo's `human:<user_id>-via-reimport` actor tag. A decision made on behalf of a member is recorded as decider plus `on_behalf_of`, never as if the member decided (Resibo `v3-deepdive-49` section 7, group-manager rule, adapted).

### 6.10 Kodo health contract

Liveness: resolver self-test passes (all rows of the section 6.2 matrix). Readiness: snapshot store reachable, export key available, Vault writer reachable when `vault` is enabled. Alerts: sessions stuck in `committing`; `conflicts_open` older than `reimport.session_ttl_days`; token rejection spikes. Self-test: a built-in fixture runs a full submit, diff and commit against an in-memory target.

## 7. Design: Bazaar.Client.Reimport

Client half of the feature, a separate module as BAZAAR-COMPONENTS 2.1 requires. Project `Bazaar.Client.Reimport` (with an `Avalonia` head project following the `Bazaar.Client.Portability.Avalonia` pattern). It references `Bazaar.Client.Core` contracts and the wire types only. It registers one pane module through `IPaneModule`.

Layout and rules, from Resibo `v3-deepdive-49`:

- Three columns (base, theirs, ours) only for fields in conflict. A cleanly resolved field shows one value. The client never re-derives conflicts; it renders what the server computed.
- Per field actions: take theirs, keep ours, enter a manual value. Each field decided independently.
- Progress count, for example "12 of 47 conflicts resolved", and resume after closing (server side session, section 6.3).
- No silent accept-all. Grouped review is allowed: conflicts with the same `group_key` are shown as a group, the human reviews the group, and one explicit confirmation covers exactly that group. There is no code path that resolves a conflict without a decision.
- Stale conflicts are marked and re-presented (6.3).
- Keyboard first, with a mnemonic from the pane registry, no Ctrl chords (doc 41 rule); accessible names for each column; screen reader reads "field, base, theirs, ours".
- A shared diff component: Resibo's `DiffViewer` serves both re-import and moderation review. Bazaar has no such component. A small shared client module `Bazaar.Client.DiffViewer` (generic `{field, oldValue, newValue}` rows with per-field action callbacks) is PROPOSED here, so the Vault conflict card, the Council and this pane do not each build one. Whether to extract it now or let the pane own it first is OPEN.

Wire: the pane talks to the server through the wire types in `Bazaar.Contracts` namespace `Reimport`, never to `Bazaar.Server.Reimport`.

## 8. Design: Bazaar.Server.Narrative (optional)

### 8.1 What Resibo does

Resibo's Historian keeps two append-only tracks in the same database: a data-change track (every write, with before and after, in the same transaction) and a quantized, human-readable narrative of a receipt's life (stages such as ingested, matched, flagged, written; `NarrativeEvent` with summary and detail). Narrative is emitted from one chokepoint, the pipeline's `run_stage()` wrapper, through one summarizer per stage, so no pipeline API knows the Historian exists. Narrative atomicity is looser than the data track (atomic with the checkpoint, not with the final write). The two are kept apart on purpose: the raw log is complete, the narrative is for display. (Resibo `v3-deepdive-29-historian.md`, sections 3 to 5.)

### 8.2 Why Bazaar wants it, and why it is a separate module

Bazaar already has the raw record: the GC log with versions (doc 01), the audit log (doc 15, 48), Vault commits, Kanban transitions. What it lacks is a displayable answer to "why did this change". A Kanban task that moved to Review, a setting that changed, a Vault file rewritten by a re-import: the reason is scattered across a GC thread, an audit row and a commit message. The narrative track is a short, readable, per-subject story linking to those sources. It must stay separate from the raw log for two reasons: it must never be mistaken for evidence (the audit log is evidence), and it must be optional so an operator who does not want it pays nothing.

### 8.3 Contracts

```csharp
namespace Bazaar.Server.Narrative;

public sealed record NarrativeEvent(
    Guid EventId, string SubjectKind, string SubjectId,
    string Stage, string Summary,
    IReadOnlyDictionary<string, string> Detail,
    Principal TriggeredBy, DateTimeOffset OccurredAt,
    NarrativeLinks Links);                   // GC seq, audit seq, commit sha, reimport session

public interface INarrativeSink
{
    // Fire and forget; narrative loss never fails the caller (looser atomicity, as Resibo).
    void Emit(NarrativeEvent evt);
}

public interface INarrativeSummarizer          // one per stage, registered by the module that owns the stage
{
    string Stage { get; }
    NarrativeEvent Summarize(NarrativeContext ctx);
}

public interface INarrativeReader
{
    IAsyncEnumerable<NarrativeEvent> ReadAsync(string subjectKind, string subjectId,
        Principal reader, CancellationToken ct);
}
```

Modules: `Bazaar.Server.Narrative.Abstractions` (the four types above, no dependencies) and `Bazaar.Server.Narrative` (storage, reader with access filter, emitters). Component id `narrative`, settings `Bazaar:Narrative`. Hard dependency: `audit`. Everything else is soft: other modules reference only `INarrativeSink` and use `NullNarrativeSink` when the module is off, which is how Resibo keeps pipeline APIs ignorant of the Historian.

### 8.4 Data and rules

```sql
-- namespace: narrative, src/Bazaar.Server.Narrative/Data/Sql/narrative/0001_event.sql
CREATE TABLE narrative_event (
  event_id      uuid PRIMARY KEY,
  subject_kind  text NOT NULL,
  subject_id    text NOT NULL,
  stage         text NOT NULL,
  summary       text NOT NULL,
  detail        jsonb NOT NULL DEFAULT '{}',
  triggered_by  text NOT NULL,              -- Principal
  occurred_at   timestamptz NOT NULL DEFAULT now(),
  links         jsonb NOT NULL DEFAULT '{}'
);
CREATE INDEX narrative_subject ON narrative_event (subject_kind, subject_id, occurred_at);
```

- Append-only. Corrections append a new event. Nothing is updated or deleted, except by erasure tombstone (doc 48 flow).
- Summaries are produced by deterministic summarizers (templates over structured facts). Whether a model may write them is OPEN. If it ever does, the text carries the "unverified" label that Kodo uses for model explanations (doc 46 section 13) and is never used as an input to decisions.
- Reads follow the access of the linked sources: a viewer sees a narrative line only if they could read the GC, task or file it describes. Private DM and private Vault areas never leak through a narrative line. A line whose link the viewer cannot read is hidden, not redacted.
- Narrative is not audit. Audit remains the evidence and the only chain (hash chain, doc 15). A narrative line may cite an audit sequence number; it never replaces one.
- Re-import emits `reimport.diffed`, `reimport.conflict.decided` and `reimport.commit` stages, so a task's story reads "re-imported from sheet X; 3 fields applied; due date conflict resolved by owner (kept ours)".

Settings: `narrative.enabled` (bool, default false until the owner enables it; the module is optional), `narrative.retention_days` (int, default unlimited, floor none; shortening is audited), `narrative.model_summaries` (bool, default false, OPEN).

Audit events: `narrative.enabled`, `narrative.disabled`, `narrative.retention.changed` (all with `Principal`).

Kodo health: liveness is the sink queue not growing past `narrative.queue_max`; readiness is storage reachable; a full queue drops the oldest low-value events and counts them, never blocks the caller. Self-test: emit and read back a fixture event with the access filter on.

## 9. Design: Bazaar.Server.ArchiveSync (optional offsite option)

### 9.1 What Resibo has, and the relationship to Bazaar's backup

Resibo's Archive Sync (`v3-deepdive-32-archive-sync.md`) is a one-way convenience mirror of archived receipts to an external drive. It is idle-time, has a per-target cursor advanced only after a confirmed upload, adds date plus a short hash to external names to avoid collisions, and on a missing or revoked target notifies and pauses, never silently continuing and never recreating the folder. Retention parity is off by default: a purged internal blob is not removed externally. It never becomes a second source of truth. Separately, Resibo's blob store fans writes out to parallel backup targets (B2 and Storj, `blob_store/backup/`), and its disaster recovery restores one-of-N from any reachable target. The convergence note (CONVERGENCE.md row 18) merges Bazaar's backup as the base with Resibo's parallel multi-target and three-way-diff sync "as options".

Bazaar's backup (doc 33, `Bazaar.Server.Components.Backup`) ships one `IBackupTarget` at a time: restic to S3 or B2, or a custom target. That is Resibo's single-target shape. This module is the option for operators who want several offsite copies.

### 9.2 Modules

`Bazaar.Server.ArchiveSync.Abstractions` (`IArchiveSyncTarget`, `ISyncCursorStore`, `SyncState`, `SyncPlan`) and `Bazaar.Server.ArchiveSync` (the composite target, planner, cursor store, three-way sync). Component id `archive-sync`. Hard dependency: `backup`, `audit`. Soft: `kodo`, `notifications`.

The composite implements the existing `IBackupTarget`, so the backup component sees one target and needs no change:

```csharp
// CompositeBackupTarget : IBackupTarget
//   Name = "composite"
//   PutDirectoryAsync: fan out to every enabled child in parallel; succeed when at least
//     archive_sync.min_targets_ok children confirm; return one OffsiteSnapshot whose id maps
//     to the per-child snapshot ids stored in archive_sync_snapshot.
//   ListAsync: union by snapshot id, newest first, with per-child presence.
```

### 9.3 Behaviour (from Resibo, adapted)

1. Parallel fan-out with a per-target cursor. Each child has its own cursor, advanced only after that child confirms. A crash mid-run resumes without re-uploading or skipping.
2. Partial success is a degradation, not a failure, as long as `min_targets_ok` is met. The backup status card shows which children lag. Kodo raises an amber state for a lagging child and red when fewer than `min_targets_ok` hold the newest snapshot.
3. Notify then pause. A child that becomes unreachable or loses access is paused with a reason, the operator is notified, and it is never auto-recreated or auto-resumed. Resuming needs an explicit operator action (Resibo section 8 resolution).
4. Three-way sync reconciliation. When a child's contents differ from the expected (someone changed the external copy, or an interrupted run left partial data), the planner computes base (last confirmed manifest for that child, stored locally), ours (the local source of truth) and theirs (the child's listing). Differences resolve as: missing remotely, upload; present remotely and unchanged against base, nothing; changed remotely (drift), flagged and not overwritten without an operator decision; extra remotely, left alone and reported. A drifted object is a conflict that goes to a human, using the same decision store as Reimport (`reimport_conflict` rows with `target_kind = 'archive_object'`), so the review pane covers it.
5. One-way and non-authoritative, as Resibo says. Local backup state remains the source of truth. A restore never trusts a child over the verified manifest.
6. No retention parity by default. A pruned local snapshot is not deleted from every child unless the operator sets `archive_sync.mirror_deletions`, which is capped by the erasure window (doc 33 Conflict 8: offsite retention must stay inside `erasure.backup_window_days`). Unlike Resibo, Bazaar has an erasure window, so this setting cannot be loosened past it. Note the tension: "never delete externally" would break erasure, so mirror-deletion is subject to the window floor, not the Resibo default. This difference is deliberate.
7. External names use date plus a short content hash suffix (Resibo `v3-deepdive-32`: date plus a short hash suffix).
8. Idle-time scheduling through the jobs seam, no filesystem watch.

### 9.4 Data

```sql
-- namespace: archive-sync, src/Bazaar.Server.ArchiveSync/Data/Sql/archive-sync/0001_cursor.sql
CREATE TABLE archive_sync_target (
  target_id     text PRIMARY KEY,           -- operator-chosen id of a child
  kind          text NOT NULL,              -- 'restic-s3','restic-b2','custom',...
  enabled       bool NOT NULL DEFAULT true,
  paused_reason text NULL,
  last_ok_at    timestamptz NULL
);
CREATE TABLE archive_sync_cursor (
  target_id     text NOT NULL REFERENCES archive_sync_target,
  stream        text NOT NULL,              -- 'wal','base','vault-bundle','blobs'
  last_item     text NOT NULL,
  confirmed_at  timestamptz NOT NULL,
  PRIMARY KEY (target_id, stream)
);
CREATE TABLE archive_sync_manifest (
  target_id     text NOT NULL REFERENCES archive_sync_target,
  object_key    text NOT NULL,
  sha256        text NOT NULL,
  confirmed_at  timestamptz NOT NULL,
  PRIMARY KEY (target_id, object_key)
);
CREATE TABLE archive_sync_snapshot (
  snapshot_id   text PRIMARY KEY,
  per_target    jsonb NOT NULL              -- {target_id: child snapshot id}
);
```

### 9.5 Settings

| Key | Type | Default | Floor | Approval |
|---|---|---|---|---|
| `archive_sync.enabled` | bool | false | Data leaves the install: owned by `backup.offsite_target` rules; each child needs the same owner approval and counsel note | Yes (owner) |
| `archive_sync.min_targets_ok` | int | 1 | 1 | No |
| `archive_sync.max_parallel_targets` | int | 4 | 1 | No |
| `archive_sync.mirror_deletions` | bool | false | Total offsite retention stays inside `erasure.backup_window_days` | Yes (owner) |
| `archive_sync.drift_policy` | enum `flag`, `ignore` | `flag` | none | No |
| `archive_sync.pause_on_unreachable` | bool | true | Always true (cannot be disabled) | Not settable |

Audit events: `archive_sync.target.added`, `.paused`, `.resumed`, `.drift.flagged`, `.snapshot.fanout` (per-child result), all with `Principal`. Kodo health: readiness is at least `min_targets_ok` children reachable; liveness is a cursor advancing within `backup.check_interval_hours`; self-test fans a fixture directory to two fake targets.

## 10. Restore-drill planning inputs for Kodo (from Resibo disaster recovery)

### 10.1 What Resibo contributes

Resibo's disaster-recovery design (`v3-deepdive-33-disaster-recovery.md`) gives Bazaar three things:

1. Restore ordering that makes verification meaningful. Restore content-addressed objects first, then the database, then verify. The reverse order reports false orphans, because "every referenced object exists" would fail against an empty object store whatever the backups hold. Bazaar's analogue: blobs and the Vault repository first, then the database restore with point-in-time recovery, then verification. Doc 33 does not state this order, so adding it to the restore wizard is a PROPOSED change to doc 33 and needs the doc 33 owner (section 13).
2. A verification report with separate lists, because each implies a different repair: references with no mapping (orphaned logical ids), files with no reference or missing files (orphaned physical files), and files whose recomputed hash differs from their address (hash mismatches). `clean` is true only when all lists are empty. Resibo's lesson on what to hash: verify the hash of what is stored now, not the hash of the original upload. In Bazaar terms, verify blobs against the content hash of the stored object, and keep the original-upload hash as separate metadata.
3. A standalone integrity check, callable without a restore, run weekly on a random sample, logging at attention level and notifying but never auto-triggering a full restore (proportional response).

Also kept: one-of-N restore (a corrupt copy at one target is skipped for the next target, and a single unreachable target is degradation, not failure), parallel verification with a concurrency limit, and single-subject restore explicitly scoped so it never touches shared state. For Bazaar, the single-subject analogue is restoring one Soul's folder or one Vault path, never the GC log (section 11).

### 10.2 The numbers Resibo does not have

Resibo's deep-dive states restore time and bandwidth estimates are an open pre-launch bench task and gives no numbers (`v3-deepdive-33-disaster-recovery.md`, open item). So there is nothing to copy. What Bazaar adopts is the shape: restore time is planned from measured inputs, not guessed, and the drill records the inputs. The doc 33 target (RTO about 1 day for a full owner's PC rebuild) is PROPOSED and unmeasured.

Kodo's restore-drill planner takes these inputs. All are measured by the drill, stored per drill, and compared with the previous drill. None is a fixed number in this doc.

| Input | Source | Used for |
|---|---|---|
| Base set size, WAL bytes since the base, Vault bundle size, blob bytes, Soul folder bytes | Backup status and `BackupSetDigest` | Total bytes to move |
| Measured downlink per target (bytes per second, p50 and p10) | The drill's own transfer, per `IBackupTarget` child | Time to fetch |
| Restore throughput of Postgres base restore and WAL replay (bytes or segments per second) | Drill measurement | Time to replay |
| Blob count and verify throughput (hash and existence checks per second, at the configured concurrency) | Drill measurement | Time to verify |
| Concurrency used, and CPU, disk and memory headroom during the drill | Kodo host probes | Whether bandwidth or disk is the bottleneck |
| Number of reachable children and which have the newest snapshot | `archive_sync_*` or the single target | One-of-N fallback time |

Planning formula (PROPOSED, to be validated by the first drill): estimated restore time = fetch time + base restore time + WAL replay time + verify time + a safety factor, where the stages run in the section 10.1 order and fetch can overlap with restore only for independent streams. Estimated bandwidth need = largest of the transfer sets divided by the RTO budget. Kodo reports the estimate with its input ages, marks it "stale" when the inputs predate the last large change in set size (a configurable percentage), and flags when the estimate exceeds the stated RTO (`backup.rto_target`, PROPOSED setting, default 24 h per doc 33).

Where the drill checklist of doc 33 step 6 says "record elapsed times and byte sizes", this section defines which ones and how they feed the estimate. Drill outputs are written to a drill record that also stores the verification report, and a drill that returns `complete_with_issues` is red for the release gate that CONVERGENCE.md row 18 names ("drills as release gate" is Bazaar's base design).

Settings (referenced to owner module): `backup.rto_target` (duration, default 24 h, PROPOSED, not yet in doc 33), `backup.drill_interval_days` (existing, 90), `backup.drill_estimate_stale_pct` (int, default 25), `backup.verify_sample_pct` (int, default 5, weekly sample), `backup.verify_concurrency` (int, default 8). New keys are proposals for doc 33 and are not added there by this doc.

Audit events: `backup.verify.sampled`, `backup.verify.finding`, `backup.drill.completed` with the report hash, all with `Principal` (`service:kodo` for scheduled runs).

## 11. How all of it respects the append-only GC rule

The rule (doc 01, owner 2026-10-24): GCs are append-only, never purged, edits keep every version. The one documented exception is the restore sequence floor (doc 01, doc 33). Reimport, export, sync and narrative add none.

1. Reimport never edits or deletes a GC row, version or event. The GC target adapter is `AppendOnly`.
2. A source record that maps to a GC message already imported can produce a new message version only if all of these hold: the message is import-origin (doc 44 8.2: `source: import`), nobody has edited it since the import (ours equals base), and the version carries the "edited" marker and keeps the old body. In every other case, including any conflict, the adapter appends a new entry (a `system` note in the GC naming the source, session and difference, or a new import-origin message), and the old message stays untouched. A conflict on a GC message therefore never has an "overwrite" choice. The pane offers "keep ours" and "append theirs as a new entry".
3. Humans' and agents' messages that were not imported are never targets. Only import-origin messages are re-importable.
4. Withdrawing a re-import follows doc 44 8.8: a new version marked `withdrawn_by_import`, text kept.
5. Export reads only. It writes `export_run`, never a GC row.
6. Restore into a scratch instance, with the rollback policy of doc 33 (PROPOSED), stays doc 33's decision. ArchiveSync copies bytes and does not interpret GC data. DR verification does not write.
7. Narrative is a derived track in its own table. It stores references (GC sequence numbers), never edits the log, and is not evidence.
8. Single-subject restore (10.1) never targets the GC log.
9. Erasure: tombstones travel; a re-import never restores an erased message's content (doc 44 10, rule 4). The resolver treats a tombstoned target as read-only and the conflict as non-applicable.

## 12. What is taken from Resibo, and what is changed

| Resibo item (path) | Taken | Changed for Bazaar, and why |
|---|---|---|
| Three-way resolution rules (`v3-deepdive-30-reimport.md`) | All four outcomes, the "fields in theirs only" rule, never auto-resolve | Terms renamed (base, theirs, ours). Added the "both changed to the same value is not a conflict" row explicitly (Resibo's deep-dive 30 states it in a code comment, not among its three headline rules). |
| Snapshot reference cell (`v3-deepdive-30`) | Idea of an embedded base reference; reject on missing | Reference is signed and the base lives server side. Resibo's design embeds a hidden reference cell and uses it as the base. In Bazaar a forged token would let a file pick its own base, and many principals can submit. |
| Field projection (`core/persistence/reimport/CLAUDE.md`) | Project both sides into the export representation; report uncoercible cells | Per-target projection, because Bazaar has several target kinds. |
| `ReimportResult` fields (`v3-deepdive-30`) | Applied count, conflicts, rejected fields, errors as data | Session persisted with ids, because Bazaar review is multi-person and long. |
| Review pane (`v3-deepdive-49-reimport-diff-ui.md` sections 2 to 7) | Three-column only for conflicts, per-field actions, "12 of 47" progress, resume, grouped bulk with explicit group confirmation, on-behalf-of attribution | Avalonia, headless-tested, keyboard first. Staleness re-presentation added. Group manager becomes owner or a granted role, with `Principal` attribution. |
| Export registry (`v3-deepdive-31`) | Provider protocol, register, get, names, errors as data, unknown provider as a code | Providers are separate modules with no selector. Output is streamed files, because Bazaar exports are bundles. Bazaar allows several runs at once, each independent (PROPOSED, Bazaar's design). |
| Excel provider techniques (`v3-deepdive-31` section 3) | Formula-driven summary sheet over a raw sheet, rebuilt only on structure change, hidden token cell | Moved into `Bazaar.Server.Export.Xlsx`, which carries the spreadsheet dependency so the shared modules stay dependency free. |
| `group_export` | Scoped export with contributor display name, never a raw id, permission checked by the owning component | `Bazaar.Server.Export.ScopedSet`. Scope check is in the registry, not the provider. |
| `audit_package`, `data_portability` | Zip with filtered history and summary; full-account scope | Folded into doc 44's bundle and audit export, as providers. Outbound bomb check adopted. |
| `slsp_summary`, `quickbooks_export`, `xero_export` | Not taken | Receipt, BIR and accounting formats. Not part of Bazaar. OPEN question 4. |
| Archive sync (`v3-deepdive-32`) | Cursor after confirmed upload, notify then pause, never auto-recreate, collision-safe names, idle scheduling, non-authoritative mirror | Composite over many children, with three-way drift reconciliation, which Resibo's one-way mirror does not have (CONVERGENCE.md row 18 names this as an option). Retention parity: Resibo default is off with no limit. Bazaar caps it by the erasure window. |
| Parallel backup targets (`v3-deepdive-33`, `v3-deepdive-13`) | Fan-out and one-of-N restore | Realised as the composite `IBackupTarget`, so doc 33's backup needs no change. |
| Restore and verify (`v3-deepdive-33`) | Order, separate report lists, hash-of-stored-object lesson, one-of-N, sample verify, proportional response | Bazaar adds Postgres PITR and the Vault. The Resibo SQLite snapshot copy is not used (doc 33 uses base backup plus WAL). Numbers: none exist in Resibo, so Bazaar adopts the planning inputs, not values. |
| Historian narrative (`v3-deepdive-29-historian.md`) | Separate display track, one summarizer per stage, single emission chokepoint idea, looser atomicity | Optional module. Reader respects source access. Not evidence. Resibo emits from a pipeline stage wrapper that Bazaar lacks, so emitters call `INarrativeSink`. Resibo's data-change track is not copied: Bazaar's GC log, audit log, versions and Vault commits already serve that role. |

## 13. Security and privacy

- Re-imported and exported content is untrusted until scanned: sandboxed parse, secret scan, `external` trust, as doc 44 (8.3, 8.5). Reader-session rules of doc 15 apply to agents that read imported text.
- A signed round-trip token prevents a submitter choosing their own base. The signing key stays in the Identity Service and never appears in an export.
- Diff panes show personal field values only to a principal who could read the field. Audit entries for conflict decisions hold hashes of values for personal targets.
- On-behalf-of decisions always record both principals.
- Exports follow doc 44 exclusions (credentials, keys, passport, cookie profiles, VM disks). The framework refuses any provider output file that the doc 44 exclusion scan flags.
- ArchiveSync sends data off the install. Each child is an owner-approved offsite target with the doc 33 counsel note, and encryption is the target's (restic) encryption. A child never receives plaintext personal data that the single-target backup would not have received.
- Narrative lines inherit the access of their sources. Erasure tombstones remove narrative lines for the erased subject's content.
- Snapshot rows contain field values, so they are personal data where the target is. They follow the same retention as the session, and erased subjects' snapshot values are removed with the tombstone.
- Nothing here adds secrets to git. All keys come from the Identity Service.

## 14. Testing (xUnit v3; headless UI where UI)

- Resolver matrix: all rows of the 6.2 table, including both-changed-to-same-value, fields absent in theirs, null versus empty, type-projection round trip with no edit reporting zero changes.
- Stripped, expired and forged token tests: each rejects with no write.
- Idempotence: submitting the same source twice applies zero fields the second time and creates no conflicts.
- Stale conflict: ours changes after diff; the decision is refused until re-presented.
- GC append-only: a property test that no re-import path issues an UPDATE or DELETE against `gc_event`, `gc_message` or `gc_message_version`; an architecture test that `Bazaar.Server.Reimport` references only the GC abstractions' writer interface. Conflict on a GC target offers no overwrite.
- Vault: non-overlapping edits merge; overlapping edits yield a conflict row and a conflict card; `merge=union` never used; Constitution path waits for owner approval.
- No silent accept-all: no code path resolves a conflict without a decision record; grouped confirmation covers exactly its group.
- Export registry: unknown provider is an error code; a throwing provider does not break the caller; scope refusals; outbound zip bomb check; round-trip token embedded and verified for each format; exclusion scan.
- Provider contract tests: every provider module passes the shared base test class (doc 46 `Bazaar.Kodo.Testing` pattern).
- ArchiveSync: cursor advances only after confirmation; kill mid-run resumes; one child failing with `min_targets_ok` met stays amber; unreachable child pauses and is not auto-resumed; drift is flagged not overwritten; `mirror_deletions` cannot exceed the erasure window. Use fake targets plus a real restic run in a drill tagged for the environment.
- Restore verification: ordering violation does not report clean; bit-flip detection; one corrupt copy at one child falls back to the next; weekly sample runs and never starts a restore.
- Narrative: sink loss never fails the caller; reader hides lines the viewer cannot access; append-only; null sink when the module is off.
- Avalonia.Headless: three-way legibility (conflict shows three values, clean field shows one); resume after close; no silent batch accept; keyboard-only operation; accessible names; stale marker.
- Real PostgreSQL for commit, session and snapshot tests (doc 34).

## 15. Work packages

Each is one project or file group with its own tests. Order is by dependency.

1. `Bazaar.Server.Reimport.Abstractions`: contracts and the pure resolver, with the full matrix test. No dependencies. (First.)
2. `Bazaar.Server.ExportFramework.Abstractions`: provider, registry, request and result contracts, token types. No dependencies. (Parallel with 1.)
3. `Bazaar.Server.Narrative.Abstractions` and `NullNarrativeSink`. (Parallel with 1.)
4. `Bazaar.Server.ArchiveSync.Abstractions`: target, cursor and plan contracts. (Parallel with 1.)
5. `Bazaar.Server.ExportFramework`: registry, scope check, run store, bomb check, audit. Depends on 2.
6. `Bazaar.Server.Export.Jsonl` and `.Csv`: first providers and contract test base. Depends on 5.
7. Snapshot and token service (signing through the Identity Service). Depends on 1, 5 and doc 14.
8. `Bazaar.Server.Reimport`: session and conflict stores, migrations, service. Depends on 1, 7, doc 44 `import_item`.
9. Kanban and settings targets. Depends on 8 and doc 12.
10. GC append-only target. Depends on 8 and GC abstractions.
11. Vault git target on `VaultWriter`. Depends on 8 and doc 18 writer.
12. `Bazaar.Server.Export.Xlsx` and `.Markdown` with token embedding. Depends on 6, 7.
13. `Bazaar.Server.Export.ScopedSet`, `.AuditPackage`, `.GraphMl`, `.Portability`. Each independent after 6.
14. `Bazaar.Client.DiffViewer` (if accepted). Depends on `Bazaar.Client.Core`.
15. `Bazaar.Client.Reimport` and its Avalonia head, with headless tests. Depends on 8 wire types and 14.
16. `Bazaar.Server.Narrative`: storage, reader filter, emitters for reimport, Kanban and settings. Depends on 3.
17. `Bazaar.Server.ArchiveSync`: composite target, cursor and manifest stores, planner. Depends on 4 and the backup component.
18. ArchiveSync three-way drift reconciliation and conflict hand-off. Depends on 17 and 8.
19. Restore verification report, integrity sample job and drill planner inputs (in the backup component, per doc 33 owner). Depends on doc 33 restore runner.
20. Kodo health contracts and self-tests for each module. Depends on each module.
21. Architecture tests: module boundaries, no third-party dependencies in Abstractions and shared libraries, GC append-only property test.

## 16. Open questions

1. Owner: confirm the module names `Bazaar.Server.Reimport`, `Bazaar.Server.ExportFramework`, `Bazaar.Server.ArchiveSync`, `Bazaar.Server.Narrative`, `Bazaar.Client.Reimport`, `Bazaar.Client.DiffViewer` and the provider naming `Bazaar.Server.Export.<Format>`.
2. Owner: re-import for a source with no snapshot (a first-time Trello or Obsidian re-import whose export Bazaar never produced). Proposed: first import records the snapshot; a later import has a base; a source with no record at all is a two-way compare where every difference with a locally changed target is a conflict.
3. Doc 44 owner: reconcile `IExporter` (doc 44 section 9) and `IPortabilityExporter` (code) with `IExportProvider`, and locate or write doc 44 section 8.10 that `ImportPreview.ReplaceSourceKeys` cites.
4. Owner: any Bazaar accounting or usage exports (cost, quota) for tools like Xero or QuickBooks? Resibo's BIR/SLSP formats are not adopted.
5. Doc 33 owner: restore order (objects and Vault first, then database, then verify) and the proposed `backup.rto_target`, verify and drill-estimate settings.
6. Owner: whether narrative text may ever be model generated, and whether narrative is on by default.
7. Where the jobs abstractions seam lives (`IJobHandler`, doc 40 lists it under `Bazaar.Server.Jobs`; no such project was found in `src`).
8. Owner: should `ArchiveSync` mirror deletions ever be allowed beyond the erasure window (proposed: never)?
9. Owner and counsel: processor terms for several offsite children (doc 33 Conflict 6 grows with each child).
10. Whether the pane for Vault conflicts replaces doc 18's GC conflict card or only supplements it (proposed: supplements).
