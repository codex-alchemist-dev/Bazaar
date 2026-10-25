# Backup, Disaster Recovery and Export

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 6
Depends on: 01-gc-data-model.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 07-soul-system.md, 08-memory-system.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 17-coding-agent-harness.md, 18-vault.md · Used by: 32-remote-access-auth-and-privacy.md, 34-testing-strategy.md

## Purpose
Defines what an instance must recover after losing a disk, a host or a credential, what is recreated instead of restored, and how an instance or a Soul moves to another host. It makes company state recoverable without choosing where the owner keeps copies.

## Decided inputs
- Backup target is the operator's choice, documented, with hooks; Bazaar does not prescribe one (drafting assignment, 2026-10-25).
- GCs are append-only and never purged. Each GC has `gc_event`, `gc_message` and `gc_message_version` tables with a full copy per version (DECISIONS-LOG round 1, C09, RESOLVED).
- Vault: Markdown in a git repo is the source of truth; PostgreSQL only indexes it. Almost every durable written artifact lives there, including each agent's persistent memory (DECISIONS-LOG round 11, C38, owner statement 2026-10-25). PostgreSQL holds the GC log, structured state (tasks, usage, scores, presence) and indexes.
- Two VMs per agent (Windows and Linux), differencing disks off one golden image (owner, 2026-10-24; DECISIONS-LOG round 1, C03). Soul data lives host-side, outside the VM disk (`Bazaar.Soul.md` §2, CHANGED 2026-10-24). Soul Passport lives outside the VM disk (`Bazaar.Soul.md` §1).
- The per-agent mounted folder holds metadata plus a session-cookie profile, detached on hard taint (DECISIONS-LOG round 2, C02, RESOLVED).
- Erasure: personal fields are per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone; backup retention must equal the erasure window (round 2, C01, RESOLVED). Legal sufficiency needs counsel.
- Identity Service holds provider and GitHub App keys from the start; one server-side `ISecretStore` is owned by `14`; config holds bootstrap references only (round 2, C05, RESOLVED).
- Server is OS-agnostic; Windows is the first host; OS-specific parts sit behind providers (`CLAUDE.md`).
- Migrations run by an explicit `migrate` command; the server only checks the schema version at startup and refuses on mismatch (round 11, C44, RESOLVED).
- GC fan-out uses Postgres NOTIFY. GC log uses hash partitions by `gc_id` from day 1, with a configurable count (round 1, C10, and round 12, RESOLVED).
- Home host accepts no inbound connections by default (round 8, C30, RESOLVED). Offsite shipping is therefore outbound-only.
- Configurability rule (round 13): plain values and policy choices here are settings with safe defaults. Legal or privacy exposure (data leaving the install, retention) defaults to the most protective option and is marked needs counsel.

## Design
Everything below is [PROPOSED] unless tagged DECIDED.

**What is restored, recreated or escrowed**

| Component | Treatment | Method |
|---|---|---|
| PostgreSQL (GC log, structured state, reputation, Kanban, usage, Passport ciphertext, DEK table, audit) | Restore | Base backups plus WAL archive (point-in-time recovery). Nightly `pg_dump -Fc` as a logical copy only |
| Vault repo (all agent memory, decisions, Constitution, research) | Restore | Push to a private remote the operator controls, plus nightly `git bundle` through `IGitProvider`. Index rebuilt, not restored. Its RPO is the last push (`vault.push_cadence`, doc 18), not the WAL age (Q6) |
| Soul folders (host-side) | Restore | File snapshot in the same run as the database, with a consistency marker. Cookie profiles are excluded unless `security.backup.include_cookie_profiles` is true (doc 15, default false) |
| Golden parent disk per version in use | Restore, or rebuild | Offsite copy only if `backup.golden_parent_offsite` is true; otherwise recipe rebuild (`16`) |
| Child VM disks | Recreate | Not backed up by default (`backup.include_child_vm_disks`, false). Unpushed in-VM work is lost; push before rebase (`17`) |
| VM checkpoints | Not a backup | Short rollback aid only |
| Keys (section Keys) | Escrow | Offline sealed copies |
| Audit hash head | Restore | Also copied offsite when `backup.audit_head_offsite` is true (`15`) |
| Logs, raw usage (`gw_usage`, `05`/`23`), search indexes | Not backed up | Logs rotate; raw usage ages out; indexes (Postgres full-text, `halfvec`) rebuilt from the log and the Vault |
| Config | Restore | Holds bootstrap references only (`03`, `14`): the KEK and database credential by reference, never values |

**Operator hooks.** `IBackupTarget` (offsite store) and `IBackupEngine` (base and WAL) are providers. Optional pre-backup and post-backup commands let an operator run their own tools.

**PostgreSQL, Windows path.**
- `pg_basebackup` nightly (plain or tar) into a backup folder.
- `wal_level = replica`, `archive_mode = on`, `archive_timeout` from `backup.wal_archive_timeout` (15 min).
- `archive_command` calls a small Bazaar archiver (fsync, copy, size check, non-zero exit on any doubt), not `cmd copy`, to avoid quoting and silent-success failures.
- Offsite shipping of base sets and WAL by restic (Windows supported per secondary sources).
- `pg_dump -Fc` nightly for selective restore only; it cannot support point-in-time recovery (PostgreSQL docs, verified).
- Local retention: `backup.base_sets_kept` (2) newest base sets plus all WAL since the older one. Incremental base backups stay off until drilled.

**PostgreSQL, Linux path.** pgBackRest (Debian and Ubuntu documented; S3-compatible repositories verified) or WAL-G (Linux binaries). Selected by `IBackupEngine` on Linux hosts. Barman is not used: its Windows status is unclear.

**Keys.**

| Secret | Purpose | Custody |
|---|---|---|
| Repository password | Decrypts backup sets | Offline sealed copy only |
| KEK (Passport master key) | Unwraps per-subject DEKs (`14`) | Offline sealed copy held by owner; DPAPI on the host (bootstrap, `03`) |
| Provider and GitHub App keys | Held by the Identity Service (`14`) | Encrypted under the KEK; re-issued if the KEK is lost |

Losing the KEK means rotating every credential and losing unwrap for encrypted personal fields (`14`). Vault files are not encrypted under the KEK in this design (Q6).

**Objectives [PROPOSED].**
- RPO for PostgreSQL: `backup.offsite_rpo_target` (1 h), measured as the age of the newest WAL segment in the offsite store. A local-only archive does not count.
- Vault RPO: the age of the last successful push or bundle, set by `vault.push_cadence` (15 min, doc 18) and `backup.vault_bundle_hours` (24 h).
- RTO about 1 day for a full owner's PC rebuild, from first restore command to first agent turn.
- One agent VM: recreate from golden parent; target hours; unmeasured.

**Restore rules.**
1. Never restore in place. Restore into an empty data root or a new instance; refuse a non-empty target.
2. Prefer point-in-time recovery to the latest recoverable point over a base-only restore.
3. Rollback policy (open; C10 FOLLOW-UP, Q3). Restoring to time T discards GC entries after T and rewinds `gc.next_seq`. Clients may already have seen the discarded sequence numbers, so reusing them would break gap-free replay (`01`, `02`). Proposal:
   - Restore to a scratch instance first. Compare each GC head with every surviving source (WAL to latest, offsite audit head, client caches).
   - Replay recoverable entries; never drop them.
   - For an unrecoverable range, write a `system` entry in each affected GC recording the lost sequence range and restore time. Set `next_seq` above the highest sequence any surviving source saw. Do not reconstruct lost content.
   - Clients whose cursor is ahead get `stale_cursor` and resync (`02`).
   - Vault restores to the remote head, never an older commit.
4. Soul folders and database come from one backup run. If the consistency marker does not match, the agent starts quarantined.
5. A restored instance runs `migrate` only deliberately. Startup checks the schema version and refuses to run on a mismatch (`04`).

**Erasure and backups.** Each subject's data key (DEK) is wrapped by the KEK and kept in a separate key table (`14`, `04`). Erasure destroys the wrapped DEK, purges embeddings and writes a tombstone (`08` §9, `14`). Backup retention equals the erasure window (DECIDED, round 2, C01). The window is `erasure.backup_window_days` (doc 04; placeholder 35 days; needs counsel; a shorter window is the more protective choice). Erasure is complete when the last backup set holding that DEK has expired; until then the gap is documented. Per-person encryption covers database personal fields only. Vault files and git history are not yet covered (Q6). Offsite retention settings below are capped so that the newest offsite snapshot stays inside the window (Conflict 8).

**Offsite store.** `backup.offsite_target` (default `none`): nothing leaves the install until the operator configures a target, and setup warns that RPO is local-only until then. Documented options: restic to any S3-compatible bucket (the example), restic to Backblaze B2 (2024-dated price, unverified for 2026), or a custom `IBackupTarget`. Restic's encryption is secondary-sourced.

**Instance export and migration.** Three artifacts, not interchangeable:

| Artifact | Contents | Use |
|---|---|---|
| Backup | Everything above, encrypted | Disaster recovery |
| Soul export (`07`) | Charter, preferences, skills; no Passport, Vault memory, reputation or VM disks | Sharing one Soul |
| Instance bundle | Base backup and WAL to cutover, Vault bundle, Soul folders, golden parent references, sealed KEK, config without secret values, SHA-256 manifest | Moving to a new host |

Cutover: (1) drain turns (`03` shutdown) and stop VMs; (2) take a final base backup and verify it; (3) restore read-only on the new host and run drill checks; (4) fence the old instance by revoking GitHub App tokens and Passport leases and blocking egress; (5) switch; keep old disks until the drill passes. The advisory lock in `03` is per database and does not fence two hosts.

**Behaviour and edge cases.**
- An archive gap, missed base backup or stale Vault push raises an alert and turns the dashboard red.
- A full archive volume blocks WAL removal, so disk use is a monitored input (`backup.volume_warn_pct` 80, `backup.volume_red_pct` 95).
- Backup repositories hold Passport ciphertext and private Vault memory, and cookie profiles only if `security.backup.include_cookie_profiles` is true. Only the owner runs restores.
- Soul folders are snapshotted at a turn checkpoint, so the marker reflects a quiet point.

### Settings

Keys are install-scoped; the operator changes them in the setup wizard or advanced settings (docs 27, 32), and every change is audited (`15`). Shipped defaults are the safe ones.

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `backup.offsite_target` | enum: none, restic-s3, restic-b2, custom | none | install | operator | Yes (owner). Data leaves the install; needs counsel (processor terms) |
| `backup.wal_archive_timeout` | duration | 15 min | install | operator | No |
| `backup.offsite_rpo_target` | duration | 1 h | install | operator | No; drill checks compare against it |
| `backup.base_sets_kept` | int | 2 | install | operator | No |
| `backup.offsite_keep_daily` | int | 14 | install | operator | No; daily plus weekly must fit the erasure window |
| `backup.offsite_keep_weekly` | int | 4 | install | operator | Yes (owner). Needs counsel. Owner's earlier 8 weeks (about 56 days) exceeds the 35-day placeholder, so 4 is the default |
| `backup.golden_parent_offsite` | bool | false | install | operator | Yes (owner). Data leaves the install; needs counsel |
| `backup.include_child_vm_disks` | bool | false | install | operator | Yes (owner). Child disks hold private work |
| `backup.audit_head_offsite` | bool | true | install | operator | No. Hash only, no content; counsel to confirm |
| `backup.vault_bundle_hours` | int | 24 | install | operator | No |
| `backup.drill_interval_days` | int | 90 | install | operator | No |
| `backup.check_interval_hours` | int | 24 | install | operator | No; WAL and Vault push age checks |
| `backup.volume_warn_pct` | int | 80 | install | operator | No |
| `backup.volume_red_pct` | int | 95 | install | operator | No |

Referenced, owned elsewhere: `erasure.backup_window_days` (35, needs counsel, doc 04); `security.backup.include_cookie_profiles` (false, doc 15); `vault.push_cadence` (15 min, doc 18).

## Testing
- Real PostgreSQL 18: base backup, write rows, archive, point-in-time restore to a timestamp; GC row counts and max seq per GC match.
- Archiver: reports failure when the target is full or unwritable; PostgreSQL keeps the WAL.
- Restore refuses a non-empty target. Restored server refuses to start on a schema mismatch. Rollback writes a recovery entry and `next_seq` clears the highest seen; clients get `stale_cursor`.
- Settings validation refuses offsite retention that exceeds `erasure.backup_window_days`.
- Vault: bundle and push restore to the remote head; private memory notes are present and no older commit is restored.
- Erasure: after window expiry, an old snapshot's ciphertext and its DEK are unreadable; before it, the gap is asserted and documented.
- KEK export round trip; wrong passphrase rejected; real DPAPI on the owner's PC (`Category=Windows-host`).
- Instance bundle digest mismatch refused; old instance holds no leases after cutover.
- Avalonia.Headless: stale RPO shows red.

**Drill checklist (quarterly, `backup.drill_interval_days`).**
1. Restore latest base plus WAL to a scratch host; counts match at a fixed timestamp.
2. Newest offsite WAL age is within RPO; last Vault push is within Vault RPO.
3. `pg_restore` a dump; run a `halfvec` similarity query on a rebuilt index.
4. Recreate one agent VM from the golden parent; reattach its Soul folder; start it.
5. Decrypt one Passport item using the offline KEK copy, not the server's.
6. Record elapsed times and byte sizes.
7. Confirm restore works with offsite credentials alone and with the repository password on a clean machine.

## Work packages
1. Backup contracts and status records in `Bazaar.Contracts`; `IBackupTarget`, `IBackupEngine`. Depends on `03`.
2. Archiver helper with fsync and verify; real-Postgres tests. Depends on 1.
3. Base backup job, retention and status. Depends on 1 and `04` WP2.
4. Offsite shipper: restic adapter, fake target. Depends on 3.
5. Vault remote push and bundle through `IGitProvider`. Depends on the `18` writer.
6. Soul folder snapshot with consistency marker. Depends on `16`.
7. KEK sealed export and import; DPAPI provider. Depends on `14` WP3.
8. Restore runner: empty target, PITR target, schema check. Depends on 3, 4.
9. Rollback recovery entry and sequence floor in the GC writer. Depends on `04` WP4.
10. Separate DEK table with window expiry. Depends on `04` migration 1, `08` §9, `14` WP4.
11. Instance bundle export and import; cutover fencing. Depends on 5, 6, 7, 8.
12. Backup status card. Depends on 3 and `27`.
13. Drill runbook (documentation only). No code dependency.

## Conflicts
1. **DPAPI machine scope vs new-host recovery** (DECISIONS-LOG round 12: still open). `14` binds the KEK to the host, which protects a copied disk. Migration and rebuild on new hardware need the KEK elsewhere. A sealed export creates a second copy of the master key. Owner decision (Q1).
2. **Erasure beyond the database** (C38 FOLLOW-UP; C01 applied to database fields only). Vault files and git history keep personal memory after key destruction. Legal sufficiency is open (`Server.md` Q17). `08` §9 must replace "until expiry" with the window, and the `04` placeholder of 35 days must be confirmed by counsel.
3. **Rollback vs append-only and gap-free sequence** (C10 FOLLOW-UP). `04` treats an older restore as an incident; `01` and `02` require gap-free sequence. The policy above is a [PROPOSED] exception: `01` and `04` must record it.
4. **Windows backup unverified (largest risk).** `pg_basebackup` and `archive_command` under the service account are unverified; the PostgreSQL Windows page returned 404; pgBackRest and WAL-G are not Windows-supported. Hyper-V checkpoint and export behaviour is unverified (Microsoft pages returned 404).
5. **Soul Marketplace status.** `Bazaar.md` calls its nature DECIDED; `Bazaar.Soul.md` and `07` say PROPOSED. The Soul export is needed only if it is in v1 (`07` Q2).
6. **Third-party copies of personal data.** Offsite stores hold encrypted Passport data and private Vault memory. Processor terms and cross-border transfer need counsel for multi-person installs (`Bazaar.Server.md` §17).
7. **Second Soul mount** (`16` Q2). C02 keeps it as an explicit exception; snapshot scope depends on the answer.
8. **Offsite retention vs erasure window** (new, from the owner's recommended restic default of 14 daily and 8 weekly, about 56 days). That exceeds the 35-day placeholder in `04`. This pass caps the default at 4 weekly; counsel must confirm the window before any longer setting is allowed.

## Questions for the owner
1. KEK portability (Conflict 1). The sealed export is a second copy of the master key, so this is a key-custody call. Recommended: passphrase-sealed export for migration only; DPAPI stays the everyday provider.
2. KEK holders. Key custody. Recommended: one sealed copy held by you; optional second holder named by you.
3. Rollback policy (C10 FOLLOW-UP; still PROPOSED per DECISIONS-LOG round 13). It changes sequence semantics, so one answer is needed. Recommended: point-in-time recovery to the last recoverable point; unrecoverable ranges recorded, never reconstructed.
4. Migration credentials. Key custody; depends on Q1. Recommended: move the KEK by sealed export, then rotate provider and GitHub App keys held by the Identity Service (`14`).
5. Soul Marketplace in v1? Product scope. Recommended: no; the format is designed only (`07` Q2).
6. Erasure in Vault history (C38 FOLLOW-UP). The options are undecided: encrypted personal notes or a history rewrite. Needs counsel. Recommended: the erasure window also applies to Vault bundles and remotes; the mechanism stays open.

MOVED TO SETTINGS: backup.offsite_target, backup.wal_archive_timeout, backup.offsite_rpo_target, backup.base_sets_kept, backup.offsite_keep_daily, backup.offsite_keep_weekly, backup.golden_parent_offsite, backup.include_child_vm_disks, backup.audit_head_offsite, backup.vault_bundle_hours, backup.drill_interval_days, backup.check_interval_hours, backup.volume_warn_pct, backup.volume_red_pct; referenced from other docs: erasure.backup_window_days (04), security.backup.include_cookie_profiles (15), vault.push_cadence (18) (DECISIONS-LOG round 13)
