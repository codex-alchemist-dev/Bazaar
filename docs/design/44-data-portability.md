# Data Portability: Export, Import and Importer Modules

Status: DRAFT (2026-11-13). Principles are DECIDED (owner, 2026-11-13). Formats and pipeline are PROPOSED. Named importers are PLANNED.
Owner questions: 6 (section 14).
Depends on: 01-gc-data-model.md, 04-postgres-schema-and-migrations.md, 07-soul-system.md, 08-memory-system.md, 11-reputation-and-peer-review.md, 12-kanban-and-dependencies.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 18-vault.md, 19-constellation-and-git-solar-system.md, 23-observatory-and-usage.md, 33-backup-dr-and-export.md
Used by: 33 (instance bundle), 32 (data-subject tools), 42 (module contract, planned), 46 (module health contract, planned), 48 (erasure, planned)

## 1. Purpose

Everything an instance holds can be taken out, and almost any mass dump can be brought in. The owner keeps the data whatever happens to the platform, the host or a vendor. This doc defines:

- the export formats for each data class, and what each export proves;
- the import pipeline, from a raw dump to committed GC entries, Vault files and Kanban tasks;
- the importer and exporter plugin contract, so that every format, first-party or third-party, is a module;
- what cannot be exported, and why;
- how export and import interact with erasure and crypto-erase.

It is a user-facing portability design. It is not a backup design. A portability export is a readable copy of data in open formats. A backup (doc 33) is an encrypted, restorable copy used for recovery. Doc 33 remains authoritative for both of those, and section 3 says where the lines fall.

## 2. Decided principles (owner, 2026-11-13)

- **D1. Everything is exportable.** Every data class in section 4 has an export path. The exceptions in section 7 are not "policy gaps". Each one is a secret or a derived artifact that has a safer route, and the route is named.
- **D2. Import any mass dump.** The pipeline accepts document dumps (docx, md, txt), and the exports of common planning and knowledge tools (Milanote, Miro, Trello, Obsidian and similar). New formats are added as importer modules without changing the pipeline.
- **D3. Importers and exporters are modules.** They use the same module contract as every other extension point (doc 42, planned). First-party importers are not special. They are signed modules with declared permissions.
- **D4. Overengineered on purpose.** Parsing runs in isolation. Every import is previewed and dry-run before it commits. Every committed object carries its provenance. Re-importing the same source is a no-op, not a duplicate.

## 3. Authority: who owns what

| Topic | Authoritative doc | Notes |
|---|---|---|
| Base backups, point-in-time recovery, offsite shipping, backup keys and escrow | 33 | Unchanged by this doc |
| Instance bundle (move to a new host, cutover fencing) | 33 | Section 9 below says how it differs from a portability export |
| Soul record and Soul export format | 07 | Scope conflict with D1 is Conflict C1 (section 13) |
| Export and import formats per data class, importers, exporters, portability settings | 44 (this doc) | |
| Personal field encryption, per-subject keys, erasure mechanics | 04 (erasure), 14 (keys), 08 section 9 | Erasure flow is planned in doc 48. Section 10 here defines only the export interaction |
| Audit hash chain, retention, signed daily export (`security.audit.export_*`) | 15 | This doc defines the audit export files (section 4.6) |
| Module contract (interfaces, permissions, versioning, trust) | 42 (planned) | Importers implement it |
| Module health contract (liveness, readiness, dependencies, self-test) | 46 (planned) | Importers and exporters implement it |
| Data-subject access and deletion tools | 32, with 44 supplying the export | |

Doc 33 section "Instance export and migration" names a Soul export and an instance bundle. Those names stay. A portability export (section 4) is a third artifact. The three are not interchangeable: backup is for recovery, the instance bundle is for moving a running instance, and the portability export is for owning and reusing the data.

## 4. Export formats by data class

### 4.1 Principles for every format

- [DECIDED] Open formats only: UTF-8, LF line endings, JSON Lines (`.jsonl`) for records, CSV (RFC 4180) for flat tables, Markdown for text, and the git bundle format for the Vault. No format needs a Bazaar client to read it.
- [PROPOSED] Every file has a schema version in its manifest. A schema version is a semver string. A reader supports its own major version and the one before it.
- [PROPOSED] Every export is streamed. Nothing is loaded whole into memory. A file is cut at 1 GiB, and the cut is recorded in the manifest.
- [PROPOSED] Every file has a SHA-256 in the manifest. The manifest itself is signed (section 6.3).
- [PROPOSED] Export is a read. It writes an audit entry (kind `portability.export`) with scope, format, row counts and manifest hash. It never changes the source data.
- [PROPOSED] Scope follows the caller's rights. The owner exports everything on the instance. Any other human member exports their own personal data and the GCs and Vault areas their role can read (doc 32). Agents export nothing.

### 4.2 GC log (doc 01, 04)

The GC is the coordination record. It is append-only and never purged, so the export is append-only too.

Per GC:

- `gc/<gc_id>/events.jsonl`: every `gc_event` in ascending per-GC sequence. Fields: `seq`, `ts`, `kind`, `actor`, `payload`, `prev_event_hash` and `event_hash` (the chain the log already keeps, where doc 01 defines it).
- `gc/<gc_id>/messages.jsonl`: one line per `gc_message` with its current version pointer and its sequence.
- `gc/<gc_id>/versions.jsonl`: every `gc_message_version`, including all earlier bodies. Each line has `message_id`, `version_no`, `edited_at`, `editor`, `body` and `prev_version_hash`. Edited messages keep the "edited" marker and every prior body, as the log requires.
- `gc/<gc_id>/mentions.jsonl`: the `gc_mention` rows, so a reader can rebuild @mention history without the database.
- `gc/<gc_id>/head.json`: the last sequence, the head hash and the row counts. Import verifies these against the data it read.
- `gc/<gc_id>/transcript.md` (optional, generated): a human-readable transcript. It is never authoritative and carries a banner saying so.

Hash partitions (doc 04) are a storage detail. The export is logical and does not depend on the partition count.

Tombstones from erasure (section 10) appear as versions with `tombstone: true`. Their author, sequence and timestamps stay, as doc 04 requires.

Direct messages are two-member GCs. They export like any GC, and the owner reads all of them (doc 01).

### 4.3 Vault (doc 18)

The Vault is Markdown files in a git repository. Its export is the repository itself, plus a plain working tree:

- `vault/repo.bundle`: a `git bundle` with all refs and full history. This is the authoritative form. It keeps commit authorship, timestamps and the audit trail that commits carry.
- `vault/tree/`: the working tree at the head commit. Files keep their paths, front matter and `[[wiki-links]]` byte for byte (doc 18 round-trip rule). Attachments are included under their content hash.
- `vault/index.jsonl`: one line per file with path, title, aliases, front matter keys, outbound links, and the commit that last changed it. It is a convenience, derived from the tree, and can be rebuilt.
- `vault/private/`: the owner's export includes every agent's private memory area, as doc 18 and C38 require (the owner reads all). A non-owner export never includes another principal's private area.

Vault export is not limited to memory. It includes the Constitution, decisions, research, plans and the Archive, because all of them are Vault files (C38).

### 4.4 Kanban (doc 12)

- `kanban/boards.json`: boards and their columns. The fixed columns are Backlog, Ready, In progress, Review and Done. Blocked is an automatic marking and is exported as a derived flag, with the reason.
- `kanban/tasks.jsonl`: one line per task with id, title, body reference (a Vault path), owner, status, priority fields, labels and timestamps.
- `kanban/edges.jsonl`: dependency edges (from, to, kind).
- `kanban/transitions.jsonl`: every status transition with actor, time and the gate that allowed it (doc 12 gates).
- `kanban/tasks.csv`: a flat table for spreadsheets. It is derived and never imported back as authoritative.

Task discussion lives in the task's GC, so the GC export covers it.

### 4.5 Constellation graph (doc 19)

The graph is derived from the Vault index, GC references, Kanban and PR data. Its primary copy is those sources, so this export is a view for other tools:

- `graph/nodes.jsonl` and `graph/edges.jsonl`: node kind, source reference, label and edge kind.
- `graph/graph.graphml`: the same graph in GraphML for external tools.
- `graph/layout.jsonl`: node positions. Excluded by default (`portability.export.include_derived`, false), because positions are derived and change with every layout run.

Graph export never carries anything the viewer's principal could not read. It uses the same filter as the live projection (doc 19, doc 18 access).

### 4.6 Personal fields and the audit log

**Personal encrypted fields (doc 04, doc 14).** Personal fields are stored as per-subject encrypted payloads (`person_key` wraps a per-subject data key, DEK). Export follows section 10 and the rules here:

- `personal/<subject_id>.enc.jsonl`: the ciphertext envelopes, with the key id and the nonce. This is always produced. It is unreadable without the DEK, so it is safe to hold.
- `personal/<subject_id>.json`: the decrypted fields. Produced only when the requester holds the key (the owner, through an authenticated session) and `portability.export.allow_plaintext_personal` is true. Its default is false. When it is produced, the manifest flags the file as `plaintext_personal: true` and the audit entry records it.
- DEKs are never written to any export, in any form (section 7).

**Audit log (doc 15).** The audit log is hash-chained (`prev_hash`) and append-only. Its export is signed:

- `audit/entries.jsonl`: rows in sequence, with `prev_hash` and `hash`.
- `audit/checkpoints.jsonl`: signed checkpoints. Each holds the head hash at a time, a signature from the instance audit key, and an external anchor reference when `security.audit.export_target` is `off-host` or a public anchor is configured (doc 15).
- `audit/proofs/<range>.json`: Merkle inclusion proofs for a requested range, so one entry can be verified without the whole log.
- `audit/verify/`: a self-contained verifier for each supported platform. It checks the chain, the checkpoint signatures, and the proofs, with no network access and no instance connection.

The audit export is read-only evidence. It is never imported as live audit data (section 8.2).

### 4.7 Configuration, Souls, usage and reputation

- `config/settings.json`: every non-secret setting with its value and the audit reference of its last change. Secret references are written as names only.
- `souls/<soul_id>/`: the Soul record in the format doc 07 defines. Its scope is Conflict C1 (section 13). The Soul export excludes the Passport and VM disks in every case, because those are secrets or machine state (section 7).
- `usage/*.csv`: raw usage rows that still exist, and `reputation/*.jsonl` from doc 11. Raw usage ages out (doc 33), so the export shows what remains, not what was deleted.
- `blobs/<sha256>`: attachments referenced by GC messages and Vault files, stored by content hash, with a `blobs.jsonl` index.

### 4.8 Summary

| Data class | Primary format | Authoritative | Signed | Encrypted by default |
|---|---|---|---|---|
| GC log | JSON Lines, per GC | Yes | Manifest | Bundle |
| Vault | git bundle plus Markdown tree | Yes | Manifest | Bundle |
| Kanban | JSON Lines plus CSV | Yes (CSV derived) | Manifest | Bundle |
| Constellation | JSON Lines plus GraphML | No (view) | Manifest | Bundle |
| Personal fields | Ciphertext envelopes; plaintext only by rule | Yes (ciphertext) | Manifest | Always ciphertext |
| Audit log | JSON Lines, checkpoints, proofs, verifier | Yes (evidence) | Checkpoints | Bundle |
| Config and Souls | JSON | Yes | Manifest | Bundle |
| Usage and reputation | CSV and JSON Lines | Partial (aged) | Manifest | Bundle |
| Blobs | Raw by hash | Yes | Manifest | Bundle |

### 4.x Memory export (separate from the Soul export, DECIDED 2026-11-13)

> Framing (owner, 2026-11-13): agent memory is not a separate store. Memory items are artefacts held in Constellation and in the contents of a Soul. The memory export below selects those artefacts; it does not add a new store.

Agent memory and reputation have their own export. It is separate from the Soul export on purpose: the Soul export stays exactly as doc 33 designs it, for sharing one Soul, and it never carries memory or reputation.

- **Scope:** a Soul's Vault memory, its memory index, and its reputation history (doc 11), as written, with provenance for every item.
- **Permissioned:** only the owner, or a role the owner grants the memory export to (doc 48, roles). Each run needs that role at run time.
- **Audited:** every memory export writes a record to the audit log (doc 48) with who ran it, which Soul, the item count, and the bundle hash.
- **Encrypted per subject:** memory and reputation are personal data, so the export uses the per-subject keys in doc 48. Crypto-erased items are not exported, and the export receipt says so.
- **Never on the Soul Marketplace (owner, 2026-11-13):** agent memory and reputation must never be part of anything published to the Soul Marketplace. The marketplace export is memory-free by design and by enforcement: the publish path refuses any bundle that contains memory or reputation items, and the check is tested. The memory export is private to the company and its owner, and it has no publish target.
- **Together:** the Soul export plus the memory export cover everything a Soul holds except the Passport and VM disks (section 7). Nothing is unexportable.

### 4.y RyuseiFuda data classes: Roadmap, Workday, Orrery (added 2026-11-13)

Names are the owner's, unchanged: RyuseiFuda, with its parts Roadmap, Workday and Orrery (doc 38, doc 40). Status: the rule that these are exportable is DECIDED (decision 1, section 2). The mapping below is PROPOSED in this doc and PLANNED to build.

- **Roadmap (objective layer, doc 38 layer 3).** Objectives and phases are Vault Markdown documents written through `VaultWriter`. Export route: the Vault export in section 4.3 (Markdown in git). No separate format. Derived views are recomputed, not exported (section 7).
- **Workday (macro board, doc 38 layer 1, which is the Kanban of doc 12).** Data lives in the existing tables `tasks`, `task_edges`, `task_links`, `task_events` and `task_gate_decisions`. Export: task rows, dependency edges, links, and the event and gate-decision logs, each with its sequence and provenance. Workday reads are owner-only in the current design. Custom roles (doc 48) do not widen that without an owner decision.
- **Orrery (micro schedule, doc 38 layer 2).** Data lives in the proposed `ryusei_trigger` table and its firing history. Export: trigger definitions as rows, and firing history from the audit and journal logs. Import restores trigger definitions paused, so no trigger fires on import (PROPOSED).

### 4.z Whole-company export (added 2026-11-13, DECIDED)

The owner's requirement: a company can be exported as one transferable file, and can also be transferred directly to a new install. The export covers the whole company: config, all agents, all GCs and all data.

- **Scope.** The company configuration (settings and their audit trail, without secret values). Every agent, with its Soul, all Souls' contents including their memory artefacts and reputation (for server migration, ALL Souls are included). Every GC with its full log, versions and sequences. The Vault, Workday, Roadmap and Orrery data (section 4.y), Constellation artefacts, usage and configuration. The audit log as evidence (section 4.6).
- **Exclusions.** The section 7 exclusions still apply: live credentials, data keys, passport contents, cookie profiles, VM disks, logs, caches and indexes. Keys and secrets travel only in the sealed recovery material of doc 48 section 6, not in the company file.
- **One file.** A single encrypted archive with a manifest, a SHA-256 digest for each part, and a company signature. It is encrypted with the owner's recovery key material (doc 48 section 7). The file name and layout are a WORKING NAME, pending owner naming decision.
- **Direct transfer.** The same content can move from the old install to a new one over an authenticated connection, with cutover fencing and the digest check of doc 33. Conduit (doc 39) is the network path when it is used.
- **Permissions and audit.** Owner-only by default. It is a high-risk action: it needs the biometric step-up of doc 48 section 8 and writes an audit record with the file digest (doc 48 section 5).
- **Marketplace.** A whole-company export is never published to the Soul Marketplace (doc 07). It is private to the company and its owner.
- **Relationship to other exports.** The Soul export (doc 33) stays memory-free and is for one Soul. The memory export (section 4.x) is for artefacts of selected Souls. The whole-company export is the only export that carries all Souls' memory, and it is for the company's own migration or recovery.
- **Relationship to doc 33.** Doc 33's instance bundle is the migration bundle that holds the cutover mechanics. The whole-company export is the portable form of the same company data. Doc 33 stays authoritative for cutover, fencing and the digest rules. Where the two lists differ, this section adds data classes and does not remove doc 33 items.
- **Restore.** The file is restored through the restore wizard of doc 48 section 6 (the same wizard as disaster recovery and server migration). The restore runs the setup checks of doc 45, and Kodo verifies the result (doc 46).

## 5. The export bundle
> WORKING NAME, pending owner naming decision. The bundle layout and file names in this section are proposals.


A full export is one bundle. A bundle is a directory tree, optionally wrapped as one encrypted archive.

```
bazaar-export-<instance-id>-<utc-timestamp>/
  manifest.json          schema version, scope, file list with SHA-256, counts, plaintext flags
  manifest.sig           signature over manifest.json by the instance export key
  README.txt             generated; explains the layout; not authoritative
  gc/<gc_id>/...
  vault/...
  kanban/...
  graph/...
  personal/...
  audit/...
  config/...
  souls/...
  usage/...
  reputation/...
  blobs/...
```

- [PROPOSED] Wrapping: the default is an encrypted archive. The key is derived from a passphrase with Argon2id, and the archive uses an AEAD cipher. Plaintext bundles need a deliberate choice (`portability.export.encrypt_bundle`, default true; the floor is that personal data is never written plaintext without the owner's explicit confirmation).
- [PROPOSED] Splitting: a bundle is split into parts of at most 4 GiB. Each part has its own hash in the manifest, and the manifest lists every part in order.
- [PROPOSED] Resumption: a large export is written as a sequence of resumable chunks. A failed download restarts at the chunk, not at the beginning.

The bundle is the portability artifact. It is not a restore format. Restoring a database from a bundle is out of scope. Migrating a running instance is doc 33's instance bundle.

## 6. Verification

### 6.1 Chains

- GC: each GC's `head.json` must match the last event and the row counts read from the files.
- Vault: `git fsck` on `repo.bundle` must pass, and the head commit must match the tree.
- Audit: the verifier replays the hash chain from the first entry, checks each checkpoint signature, and checks each proof.
- Files: every SHA-256 in the manifest must match.

### 6.2 Export self-check

Before a bundle is marked complete, the server runs the verification above on the bundle it just wrote. A bundle that fails is marked `incomplete` in the audit log and is not offered for download.

### 6.3 Signatures

- The manifest is signed with the instance export key. The key is held by the Identity Service (doc 14), so it never appears in the bundle.
- The public half is published in the bundle so a reader can check the signature without the instance.
- A key rotation keeps the old public key in the bundle's history, so old exports still verify.

## 7. What cannot be exported, and why

Each row below is a hard exclusion or a route to the data. None of these is a setting.

| Item | Why it is excluded | Route instead |
|---|---|---|
| Provider API keys, GitHub App private keys, OAuth refresh tokens, TOTP seeds, recovery codes, passkeys | Credentials. A copy in a file is a leak, even encrypted by a passphrase the owner may lose | Re-issue at the destination. The Identity Service rotates keys (doc 14). The export lists which credentials are needed, by name only |
| Data keys (DEKs), the key-encryption key (KEK), the backup repository password | Keys that would make ciphertext readable, including ciphertext that erasure has already made unreadable | Escrow under doc 33 (sealed copies). Never in a portability export |
| Passport store contents and cookie profiles | Live credentials and sessions, excluded from backups by default (doc 15) | Doc 33 instance bundle, as ciphertext only, for migration. Never decrypted, never in a portability export |
| Secrets found inside imported or stored text | A secret in a note is still a secret | Detected by the secret scanner (section 8.5). Redacted in the export only when the owner asks; the redaction is logged |
| Live VM disks, child disks, golden images | Machine state, large, and some images carry OS licence terms (doc 16) | Recipes (doc 16) rebuild the environment. Images are exported only through doc 33 when enabled there |
| Erased subjects' content | Destroyed by design (section 10) | Tombstones only |
| Other principals' private Vault areas | Access rule (doc 18) | Owner export only, as section 4.3 |
| Logs, presence, cache, search indexes, embeddings | Rebuildable or transient. Logs rotate (doc 33) | Rebuilt from the log and the Vault. Embeddings can be included as derived data with their embedding profile id (`portability.export.include_derived`, false) |
| Layout positions and other derived views | Change on every run | Recomputed from sources |
| The live audit chain as a writable object | Import would forge history | Audit is exported as evidence (section 4.6) and imported only as an archive (section 8.2) |
| Work in flight (open turns, locks, leases) | Not data at rest | Drain before export (doc 03 shutdown). Export of a running instance is a snapshot at a consistent point, with the point recorded |

## 8. Import pipeline

### 8.1 Stages

```
upload -> stage -> detect -> parse -> map -> preview -> dry-run -> commit -> audit -> verify
```

1. **Upload.** The owner uploads a file, a folder archive, or points the importer at a path the server can read. The upload is written to a staging area with a quota. Its SHA-256 is recorded in the import job. Nothing is interpreted yet.
2. **Stage.** The job record is created with its id, actor, source manifest, and size limits. Staging files are encrypted at rest with the install key (doc 14 DEK for the import subject), and their retention follows section 8.6.
3. **Detect.** Format detection reads magic bytes and structure, never the file extension alone. Each importer module offers a `Detect` that returns a confidence and a short reason. A zip file with `[Content_Types].xml` and `word/document.xml` is a docx candidate. A folder with `.obsidian/` is an Obsidian vault candidate. Several candidates above threshold make the owner choose. Detection runs inside the sandbox (section 8.3).
4. **Parse.** The chosen importer streams source records. Each record has a stable source key (section 8.4), a source type, and its raw content. Parsing never writes to the instance.
5. **Map.** The importer's mapping rules turn records into proposed objects: GC entries, Vault files, Kanban tasks, graph hints. Mapping is pure. It has no side effects, and its rules are versioned and hashed.
6. **Preview.** The preview shows counts per target, sample items for each target (about 20 per target), collisions (title, path, Kanban column), unmapped items with the reason, secret findings, and encoding problems. The owner can change mapping choices here: a Trello list to a Kanban column, a folder to a Vault path, a chat dump to an existing GC. A change re-runs the mapping only, not the parse.
7. **Dry-run.** The full commit path runs against a scratch target: a scratch Vault branch, and GC writes into a transaction that is rolled back. It reports what would be written, what would conflict, and how long a commit would take. The dry-run result is hashed and stored with the job. Commit is allowed only against a dry-run result that matches the current mapping hash.
8. **Commit.** Commit runs in batches (section 8.7). Each batch is one transaction for GC writes (the append path of doc 04) and one commit for Vault files, through VaultWriter (doc 18). A failed batch is rolled back and the job stops at a known point.
9. **Audit.** Each batch writes one audit entry (kind `portability.import.batch`) with the job id, batch number, item count and the batch's content hash. Each object created records its import job id and source key in its provenance.
10. **Verify.** After commit, the importer's verifier re-reads what it wrote and checks counts and hashes. A mismatch marks the job `failed_verify` and raises an alert (section 11).

### 8.2 Import targets and rules

| Target | Source examples | Rule |
|---|---|---|
| Vault note | docx, md, txt, Obsidian notes, Milanote and Miro pages as Markdown | New file at the mapped path. Wiki-links resolve against the target Vault. Front matter gains `source` and `imported_job` keys. Existing paths are never overwritten: a collision becomes a new path with a suffix, shown in preview |
| GC entry | Chat dumps (txt with timestamps), Trello card comments, exported discussions | Imported as entries with `source: import` and their original timestamps kept in the payload. The entry's real sequence is assigned at commit. Original timestamps are shown as imported, never as live |
| Kanban task | Trello cards, Miro sticky notes with status, planning-tool tasks | Mapped to Backlog, Ready, In progress, Review or Done. An unknown column goes to Backlog with a preview warning. Dependencies become edges only when both ends are in the same job or already exist |
| Graph hint | Miro connectors, Obsidian links, Milanote connections | Not stored. Links become wiki-links inside Vault notes, and the graph is rebuilt (doc 19) |
| Personal fields | Bazaar export of the same instance | Only into the same subject, with that subject's key. A subject that does not exist on the target is not created by import. A personal field for an erased subject is never restored (section 10) |
| Audit log | Bazaar audit export | Never merged into the live chain. Imported as an archive under `archive/audit/<job>`, with a verification status. The archive is marked as not part of the live chain |
| Config | Bazaar `settings.json` | Previewed as a diff against the current settings. Applied only through the settings registry (doc 03) with the floor checks. Secrets are never applied, only listed as needed |

Provenance is always attached. Every imported object carries `source` (importer id and version), `import_job`, `source_key` and `source_hash`. Nothing from an import looks native.

### 8.3 Parser isolation

Parsing untrusted files is the riskiest step, so it runs in a separate process:

- The parser runs as a child process with no network access, with read access only to its staging input, and write access only to its output stream. It runs under OS confinement (process isolation on the host, with the provider interface of doc 03). Module trust follows doc 42: a signed module with declared permissions.
- Limits are checked before and during parsing: maximum total bytes, maximum file count, maximum nesting depth, a maximum expansion ratio for compressed content (`portability.import.max_expansion_ratio`, default 100:1), a maximum single-record size, and a wall-clock timeout per record.
- Archives are read with path checks. Absolute paths, `..` segments and symlinks are rejected. Hard links and device files are rejected.
- Document formats are parsed without macros, without external entity resolution, and without executing embedded content. Embedded objects are listed in the preview and not extracted.
- A parser crash fails only the job. The host restarts the parser with the job's checkpoint (crash-only design, doc 46 health hooks).

### 8.4 Idempotent re-import

Re-importing the same source must not create duplicates.

- Every record has a `source_key`. Where the format offers a stable id, it is used: a Trello card id, a Miro item id, an Obsidian relative path. Otherwise the key is the path inside the source archive plus a record index.
- The `import_item` table (new, planned under doc 04) holds `(importer_id, source_key, content_hash, target_kind, target_id, target_version, job_id)`.
- On re-import the rule is:
  - same key and same content hash: no-op, reported as `unchanged`;
  - same key and changed content: a new version of the target (a new GC message version with the edited marker, a new Vault commit, or a Kanban update with a transition record). The old version is kept. Preview shows it as `changed`;
  - a new key: a new target;
  - a key that disappeared from the source: nothing is deleted. Preview shows it as `missing from source`, and the owner can choose to withdraw it (section 8.8).
- Importer versions are part of the key only for mapping, not for identity. A mapping change re-maps and shows the diffs in preview.

### 8.5 Secrets and personal data in imports

- The secret scanner runs on every text record. It looks for key-shaped strings, private key blocks, and connection strings. Findings are shown in preview with the source location.
- Policy (`portability.import.secret_scan`, default `quarantine`): a record with a finding is committed to a quarantine area, not to the target, until the owner decides. Options are `import as is`, `redact and import` and `skip`. Each decision is audited.
- Imported text is external content. It carries trust `external` (doc 15). Agents that read imported notes do so in reader sessions, and high-impact actions after reading them need owner approval (doc 15, taint).
- The import job record stores hashes and locations, never the plaintext of imported personal content.

### 8.6 Staging retention

- Staging holds the source as uploaded and the parse output until commit verifies.
- Default: staging is deleted when the job commits and verifies. The job keeps the source manifest, hashes and counts.
- `portability.import.staging_retention_hours`, default 0. The floor is 72 hours unless the owner approves a longer period, because staging holds plaintext personal content.

### 8.7 Large dumps

- **Streaming everywhere.** Parsers yield records; nothing holds a whole dump in memory. Staging is on disk with a quota.
- **Batches.** Commit batches are sized by items and bytes (`portability.import.batch_items`, default 500, and a byte cap). A batch is one transaction or one commit, so progress is durable.
- **Checkpoints and resume.** Each batch records its last source key. A job can be paused, resumed after a restart, or cancelled. A resume skips batches already committed (their `import_item` rows exist) and re-verifies the last one.
- **Priority.** Import runs at low priority behind live GC traffic, with an upper bound on writes per second so that chat latency stays inside its budget. The rate is a setting.
- **Cancel means stop, not undo.** GC history is append-only, so cancelling leaves committed batches in place. Undo is a separate action (section 8.8).
- **Progress and health.** Progress (items, bytes, ETA, failures) is reported to Observatory (doc 23) and to the health contract (doc 46). A job that makes no progress for a set time is marked `stalled` and alerted.
- **Size limits.** `portability.import.max_upload_bytes`, default 10 GiB. A larger dump is split by the owner into jobs, or the owner raises the limit, which is audited.

### 8.8 Withdrawing an import

Undo never deletes history:

- GC entries from an import get a new version that marks them `withdrawn_by_import: <job>`. The text stays in history, and the view hides it by default.
- Vault files from an import are reverted by a new commit that restores the prior state of each path. The history stays.
- Kanban tasks from an import get a transition to Blocked with reason `withdrawn by import`, or are archived, following doc 12 gates.
- The withdrawal is itself an audit entry, linked to the job.

### 8.9 Import audit trail

Each job record holds:

- job id, actor, start and end time, final state;
- source manifest: names, sizes, SHA-256 of each file;
- importer id and version, mapping rules hash, dry-run result hash;
- batch list: number, item counts by target, first and last source key, batch hash;
- preview decisions made by the owner, with time;
- verify result and any failed items with reasons.

The job record is itself exported in the portability bundle, so the owner can prove where each object came from.

## 9. Importer and exporter plugin contract

Importers and exporters are modules. They use the module contract of doc 42 (planned) and the health contract of doc 46 (planned). This section defines what is specific to portability. The interfaces are in the shared contracts assembly, so a third-party module needs only the contracts package.

```csharp
// Sketch. Names are PROPOSED. Final shapes are set when doc 42 and doc 46 are written.
public interface IImporter : IBazaarModule
{
    string FormatId { get; }                         // for example "trello.json"
    ValueTask<DetectResult> DetectAsync(SampleView sample, CancellationToken ct);
    IAsyncEnumerable<SourceRecord> ParseAsync(ParseInput input, ParseContext ctx, CancellationToken ct);
    MappingResult Map(SourceRecord record, MappingRules rules);   // pure, no I/O
    ValueTask<VerifyResult> VerifyAsync(VerifyInput input, CancellationToken ct);
}

public interface IExporter : IBazaarModule
{
    string FormatId { get; }                         // for example "gc.jsonl.v1"
    IAsyncEnumerable<ExportFile> ExportAsync(ExportScope scope, CancellationToken ct);
}
```

Rules for every importer and exporter:

- **Declared permissions.** An importer declares what it reads (the staging input, nothing else) and what it proposes to write (target kinds). It cannot ask for network access, instance secrets or other modules' data. The install-time approval shows the declaration (doc 42).
- **Pure mapping.** `Map` does no I/O. This makes the dry-run and the preview exact, and makes mapping testable without a database.
- **Sandboxed parse.** `ParseAsync` runs in the parser process of section 8.3 unless the module is first-party and signed, in which case it still runs in a separate process. Parsing is never in the server process.
- **Versioned.** `FormatId` and the module version are both recorded in provenance. Mapping changes bump the minor version and need contract tests against stored fixtures.
- **Health.** Each module exposes liveness, readiness, dependencies (the sandbox runner, for example) and a self-test that parses a built-in fixture. Health is enforced by contract tests (doc 46).
- **Trust tiers.** Verified tag and unverified modules are shown as in doc 42. An unverified importer installs with a warning and runs under the same sandbox.
- **Licence.** The AGPL module exception applies: an importer that uses only the public module API may be under any licence, including a closed commercial one. Its licence is shown at install (doc 42).

Exporters follow the same rules, and their output is streamed as `ExportFile` items with a SHA-256 added by the bundle writer, not by the exporter.

## 10. Relationship to erasure

Erasure is planned in doc 48. Its mechanics are in doc 04 (erasure hook), doc 08 section 9 and doc 14. The rules below only define how exports behave under erasure. The erasure window is `erasure.backup_window_days` (doc 04, placeholder 35 days, needs counsel).

1. **Exports carry ciphertext, not keys.** Personal fields in any export are ciphertext plus a key id (section 4.6). DEKs are never exported. After crypto-erase destroys a subject's DEK, the ciphertext in every existing export is unreadable. This is the main guarantee: an export does not defeat erasure of encrypted personal fields.
2. **Plaintext personal exports are outside erasure.** A decrypted export (`personal/<subject>.json`) is a copy erasure cannot reach. So it is off by default, needs the owner's session, is flagged in the manifest, and is audited. Erasure cannot recall such a copy. The owner is told this when the file is produced.
3. **Erasure records which exports hold a subject.** Each export manifest lists the subjects whose ciphertext it holds. When a subject is erased, the erasure proof (doc 48) lists the export manifest ids that contain that subject, so the owner knows which exports are now unreadable and which plaintext copies exist.
4. **Tombstones travel.** An erased message's tombstone is exported and imported as a tombstone. Import never restores the content of an erased message, even if the source file still has it in plaintext. The importer detects the subject or message id against the tombstone list and writes only the tombstone.
5. **No resurrection by key.** Import of a personal-field envelope for an erased subject does not recreate the subject's DEK. The envelope stays unreadable.
6. **Plaintext in the Vault (open).** Vault files are not encrypted under the KEK in the current design (doc 33 Q6). A Vault export therefore carries personal text in plaintext, and erasure in the Vault and its git history is open. Until doc 33 Q6 is answered, the owner's Vault export is a plaintext copy of whatever the Vault holds. The manifest says so.
7. **Imports are new subjects.** Importing text that names a person creates no personal-field subject. Any personal data inside imported notes is Vault content, and it follows the Vault rules above.
8. **Source staging.** Staging holds plaintext (section 8.6) and is deleted at commit, so erasure has nothing to reach there.

## 11. Health, alerts and audit

- Import and export jobs appear in Observatory (doc 23) and in the health contract (doc 46). Kodo (the health module, DECIDED) reads job status and raises alerts for `failed_verify`, `stalled`, and parser crash loops.
- Every export and import writes audit entries (section 4.1, section 8.9). Plaintext personal exports and secret-scanner decisions are audited with the actor.
- A job that holds the owner's approval needed for a plaintext export or a size-limit raise stops at that decision and waits for it.

## 12. Settings

Keys are install-scoped. The operator changes them in the setup wizard or advanced settings, and every change is audited (doc 15). Floors cannot be loosened. A setting can only tighten a floor.

| Key | Type | Default | Floor | Who may change | Needs approval |
|---|---|---|---|---|---|
| `portability.export.encrypt_bundle` | bool | true | Personal data is never written plaintext without explicit owner confirmation | Operator | Yes (owner) to set false |
| `portability.export.allow_plaintext_personal` | bool | false | Owner session only; always audited and flagged | Owner | Yes (owner) |
| `portability.export.include_derived` | bool | false | Derived data never claimed as authoritative | Operator | No |
| `portability.export.part_bytes` | int | 4 GiB | Max 4 GiB | Operator | No |
| `portability.import.max_upload_bytes` | int | 10 GiB | Hard maximum set by host storage | Operator | Yes (owner) to raise |
| `portability.import.max_expansion_ratio` | int | 100 | Min 10 | Operator | No |
| `portability.import.batch_items` | int | 500 | 1 to 5000 | Operator | No |
| `portability.import.write_rate` | int | 200 items per second | Bounded by chat latency budget | Operator | No |
| `portability.import.secret_scan` | enum: `quarantine`, `warn`, `off` | `quarantine` | `off` not allowed for text from external sources | Operator | Yes (owner) to set `warn` or `off` |
| `portability.import.staging_retention_hours` | int | 0 | Max 72 unless owner approves | Operator | Yes (owner) above 72 |
| `portability.import.dry_run_required` | bool | true | Always true (cannot be disabled) | None | Not settable |

## 13. Conflicts with existing docs

Conflicts are listed, not silently resolved. Where a conflict needs the owner, it is also in section 14.

- **C1. "Everything is exportable" (D1) vs the Soul export scope (doc 33, "Soul export (`07`)"; doc 07).** Doc 33 says a Soul export has no Passport, Vault memory, reputation or VM disks. D1 says everything is exportable. RESOLVED by the owner (2026-11-13): the Soul export keeps doc 33's exact scope (no Vault memory, no reputation, no Passport). Agent memory and reputation get their own separate export, the memory export, which is permissioned and audited (section 4). Together the two exports mean nothing is unexportable. The Passport stays excluded from every portability export (section 7).
- **C2. Export vs instance bundle (doc 33, "Instance export and migration").** Doc 33 defines an instance bundle that contains the database base backup, WAL, Vault bundle, Soul folders and sealed KEK. This doc defines a different artifact with the same name family. Resolution: doc 33 keeps the instance bundle, and this doc calls its own artifact a portability export. Doc 33 should add a one-line pointer to this doc. Not a silent rename.
- **C3. Vault plaintext vs personal encryption.** Doc 33 Q6 and doc 18 keep Vault files outside KEK encryption. D1 and the personal-field rule (doc 04) imply personal content is protected. A Vault export carries Vault text in plaintext until Q6 is answered (section 10, rule 6). Needs counsel and owner decision.
- **C4. Erasure flow owner.** Doc 04 line 71 and doc 33 say erasure is "owned by docs 32 and 33". The brief assigns erasure to doc 48, which is in the same PR. This doc follows doc 48 for the flow, and doc 04 and doc 33 must be updated when it exists.
- **C5. Erasure "backup window" vs exports.** Doc 04 and doc 33 bound erasure reach by the backup window. Exports are a copy outside the window. Section 10 rule 2 and rule 3 cover this. The window does not cover exports, and counsel must be told.
- **C6. Doc 15 audit export vs this doc's audit export.** Doc 15 says the audit export is daily and signed by default (`security.audit.export_target`, default `local`). Section 4.6 defines the files of the audit export. Both agree on signing. Doc 15 keeps its daily export schedule and retention. This doc owns the file layout and the verifier.
- **C7. Cookie profiles.** Doc 15 excludes cookie profiles from backups by default (`security.backup.include_cookie_profiles`). Under D1 they would be exportable. Section 7 excludes them from every portability export, with no setting, because they are live sessions. Doc 15 should note this. Owner may disagree (Q5).
- **C8. Referenced docs not in the design set.** The brief references docs 42, 46 and 48. The design set in `docs/design/README.md` ends at 38 and has no 42, 46 or 48. Those references are to planned docs. Their contract names are used here as placeholders.

## 14. Questions for the owner

1. **Soul export scope (C1).** Recommended: keep doc 33's Soul export for sharing (no Vault memory, no reputation, no Passport), and include all of a Soul's memory and reputation in the owner's full export. Reason: the two exports have different readers. Alternative: every Soul export includes memory, which makes sharing a Soul also share its private memory.
2. **Plaintext personal exports.** Recommended: off by default, owner session only, flagged and audited, with the warning that erasure cannot recall such a copy. Reason: this is the only place where D1 and erasure pull against each other.
3. **Vault personal text (C3, doc 33 Q6).** Recommended: the erasure window applies to Vault bundles and remotes, and the mechanism stays open until counsel answers. Reason: the export cannot be more protected than the Vault is.
4. **Secret scanning on imports.** Recommended: `quarantine` by default, with owner decisions per record. Reason: imported dumps are the most likely place for leaked keys to reach the instance.
5. **Cookie profiles in portability exports (C7).** Recommended: never exported. Reason: live sessions are credentials. Alternative: allowed in the owner's full export under the same plaintext rule as personal data.
6. **Import of audit archives.** Recommended: allowed as read-only archives, marked as outside the live chain. Reason: owners who move instances need their history. Alternative: refuse audit import entirely.

## 15. Testing

- **Round trip.** Export a seeded instance (GCs with edits, a Vault with wiki-links and private areas, a Kanban board with dependencies, personal fields for two subjects, an audit chain). Import into an empty instance. Compare GC sequences, message versions, Vault file bytes, Kanban states and personal fields (readable only with the key).
- **Chains.** The verifier rejects a bundle with a changed event, a changed manifest line, a missing checkpoint and a broken proof. It accepts an untouched bundle with a valid signature, and rejects it when the signing key is wrong.
- **Exclusions.** No export contains a DEK, KEK, provider key, App private key, TOTP seed, recovery code, passkey or cookie profile. A scan of every produced file asserts this on a seeded instance that holds all of them.
- **Erasure interaction.** After crypto-erase of a subject, the ciphertext in an earlier export is unreadable. A plaintext export is flagged in the manifest and listed in the erasure proof's export list (doc 48, once written). Importing an export with an erased subject restores the tombstone only.
- **Idempotent re-import.** Importing the same source twice creates no new targets and reports every item as `unchanged`. Changing one source file creates exactly one new version of one target.
- **Parser isolation.** Zip bomb, zip-slip path, symlink, macro-bearing docx, malformed XML, and a parser that hangs or crashes. Each fails the job cleanly, with no instance write and no host crash. Use real PostgreSQL for the commit tests (doc 34).
- **Dry-run.** A commit against a stale mapping hash is refused. A dry-run leaves no GC rows and no Vault commits.
- **Large dump.** A synthetic dump of several hundred thousand records runs in batches, pauses and resumes at a batch boundary, and commits each batch once. Live chat latency stays inside its budget while the import runs (performance test, doc 34).
- **Withdrawal.** An import withdrawal leaves the history intact and hides the withdrawn items.
- **Avalonia.Headless.** Preview shows collisions and secret findings; the commit button stays disabled until the dry-run matches the mapping hash.

## 16. Work packages

1. Portability contracts in the shared contracts assembly: `IImporter`, `IExporter`, `SourceRecord`, `MappingResult`, `ExportFile`, job and batch records. Depends on doc 42 and doc 46 contract drafts.
2. Bundle writer: streaming, manifest, SHA-256, parts, optional encryption. Depends on 1.
3. Verifier: chain, signature and proof checks, shipped as a self-contained build per platform. Depends on 2 and doc 15.
4. GC exporter (`gc.jsonl.v1`) with self-check. Depends on 2 and doc 04.
5. Vault exporter (`repo.bundle` and tree). Depends on 2 and doc 18.
6. Kanban and graph exporters. Depends on 2, doc 12 and doc 19.
7. Personal field exporter (ciphertext always; plaintext rule). Depends on 2 and doc 14.
8. Audit exporter, with checkpoints and proofs. Depends on 3 and doc 15.
9. Import job store: job, batch, `import_item` tables (migration, planned under doc 04). Depends on doc 04.
10. Parser sandbox and limits. Depends on doc 03 providers and doc 42 trust.
11. Mapping engine and preview model; pure mapping tests. Depends on 1.
12. Commit path: batches, dry-run, verify, withdrawal. Depends on 9, 11, doc 01 GC append and doc 18 VaultWriter.
13. Generic importers: md, txt and docx (PLANNED). Depends on 10 to 12.
14. Named importers (PLANNED): Obsidian vault, Trello JSON, Miro export, Milanote export. Each starts from a verified real sample. Format details are not assumed in this doc. Depends on 13.
15. Doc 42 and doc 46 compliance: contract tests for every importer and exporter. Depends on 1 and the module contract.

## 17. What is PLANNED

Nothing in this doc is built. The status of each part:

- DECIDED: everything is exportable (D1); importing any mass dump (D2); importers and exporters as modules (D3); the exclusions in section 7 as hard rules.
- PROPOSED: the bundle layout, formats, signing, verification, the import stages, idempotent re-import, the sandbox limits, the settings table, the plugin interfaces in section 9 (sketch only).
- PLANNED, named importers: Obsidian vault, Trello (JSON export), Miro (board export), Milanote (board export). Each is a module, starts from a real sample taken by the owner, and is gated by the same tests as any other importer. Their format details are verified when built, not before.
- PLANNED, generic importers: docx, md and txt, and chat-log text dumps.
- PLANNED, derived exports: the graph GraphML file and the optional Parquet analytics export. Neither is needed for v1.
- PLANNED, dependencies: doc 42 (module contract), doc 46 (health contract), doc 48 (erasure). This doc follows their drafts when they exist.
