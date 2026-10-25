# Backup, Disaster Recovery and Export

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 01-gc-data-model.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 08-memory-system.md, 14-identity-service-and-passport.md, 16-agent-vms-and-environments.md, 18-vault.md, 07-soul-system.md · Used by: 32-remote-access-auth-and-privacy.md, 34-testing-strategy.md

## Purpose
Defines what an instance must recover after losing a disk, a host or a credential, what is recreated instead of restored, and how an instance or a Soul moves to another host. It makes company state recoverable without choosing where the owner keeps copies.

## Decided inputs
- Backup target is the operator's choice, documented, with hooks; Bazaar does not prescribe one (drafting assignment, 2026-10-25).
- GCs are append-only and never purged; edits keep every version (`CLAUDE.md`; `Bazaar.md` §2).
- Vault: Markdown in a git repo is the source of truth; PostgreSQL only indexes it (`CLAUDE.md`; `18-vault.md`).
- Two VMs per agent (Windows and Linux), differencing disks off one golden image; Soul data lives host-side, outside the VM disk (`Bazaar.Soul.md` §2, CHANGED 2026-10-24).
- Soul Passport lives outside the VM disk (`Bazaar.Soul.md` §1).
- Server is OS-agnostic; Windows is the first host; OS-specific parts sit behind providers (`CLAUDE.md`).
- Memory, reputation and Kanban live in PostgreSQL (assignment; `08-memory-system.md`).

## Design
Everything below is [PROPOSED] unless tagged DECIDED.

**What is restored, recreated or escrowed**

| Component | Treatment | Method |
|---|---|---|
| PostgreSQL (GC log, memory, Kanban, reputation, Passport ciphertext, audit) | Restore | Base backups plus WAL archive (point-in-time recovery). Nightly `pg_dump -Fc` as a logical copy only |
| Vault repo | Restore | Push to a private remote the operator controls; nightly `git bundle`. Index rebuilt, not restored |
| Soul folders (host-side) | Restore | File snapshot in the same run as the database, with a consistency marker |
| Golden parent disk per version in use | Restore, or rebuild | Offsite copy of each parent a child references; recipe rebuild as fallback (`16`) |
| Child VM disks | Recreate | Not backed up. Unpushed in-VM work is lost; push before rebase (`17`) |
| VM checkpoints | Not a backup | Short rollback aid only |
| Keys (section Keys) | Escrow | Offline sealed copies |
| Audit hash head | Restore | Also copied to the offsite store (`15`) |
| Logs, raw usage, search indexes | Not backed up | Logs rotate; raw usage ages out (`04`); indexes rebuilt |
| Config | Restore | Holds secret references only (`03`), so it backs up without secret values |

**Operator hooks.** `IBackupTarget` (offsite store) and `IBackupEngine` (base and WAL) are providers. Optional pre-backup and post-backup commands let an operator run their own tools.

**PostgreSQL, Windows path.**
- `pg_basebackup` nightly (plain or tar) into a backup folder.
- `wal_level = replica`, `archive_mode = on`, `archive_timeout = 15min`.
- `archive_command` calls a small Bazaar archiver (fsync, copy, size check, non-zero exit on any doubt), not `cmd copy`, to avoid quoting and silent-success failures.
- Offsite shipping of base sets and WAL by restic (Windows supported per secondary sources).
- `pg_dump -Fc` nightly for selective restore only; it cannot support point-in-time recovery (PostgreSQL docs, verified).
- Retention: two newest base sets plus all WAL since the older one. Incremental base backups stay off until drilled.

**PostgreSQL, Linux path.** pgBackRest (Debian and Ubuntu documented; S3-compatible repositories verified) or WAL-G (Linux binaries). Selected by `IBackupEngine` on Linux hosts. Barman is not used: its Windows status is unclear.

**Keys.**

| Secret | Purpose | Custody |
|---|---|---|
| Repository password | Decrypts backup sets | Offline sealed copy only |
| KEK (Passport master key) | Unwraps per-Soul data keys (`14`) | Offline sealed copy held by owner; DPAPI on the host |
| GitHub App keys and other credentials | Inside the Passport | Encrypted; re-issued if the KEK is lost |

Losing the KEK means rotating every credential. Memory and history survive (`14`).

**Objectives [PROPOSED].**
- RPO about 1 hour, measured as the age of the newest WAL segment in the offsite store. A local-only archive does not count.
- RTO about 1 day for a full NUC rebuild, from first restore command to first agent turn.
- One agent VM: recreate from golden parent; target hours; unmeasured.

**Restore rules.**
1. Never restore in place. Restore into an empty data root or a new instance; refuse a non-empty target.
2. Prefer point-in-time recovery to the latest recoverable point over a base-only restore.
3. Rollback policy. Restoring to time T discards GC entries after T and rewinds `gc.next_seq`. Clients may already have seen the discarded sequence numbers, so reusing them would break gap-free replay (`01`, `02`). Proposal:
   - Restore to a scratch instance first. Compare each GC head with every surviving source (WAL to latest, offsite audit head, client caches).
   - Replay recoverable entries; never drop them.
   - For an unrecoverable range, write a `system` entry in each affected GC recording the lost sequence range and restore time. Set `next_seq` above the highest sequence any surviving source saw. Do not reconstruct lost content.
   - Clients whose cursor is ahead get `stale_cursor` and resync (`02`).
   - Vault restores to the remote head, never an older commit.
4. Soul folders and database come from one backup run. If the consistency marker does not match, the agent starts quarantined.

**Erasure and backups.** Each subject's data key (DEK) is wrapped by the KEK. Erasure destroys the wrapped DEK, purges embeddings and writes a tombstone (`08` §9, `14`). Backup snapshots still hold the wrapped DEK, and the escrowed KEK can unwrap it. Erasure therefore completes only when every snapshot holding that DEK has expired. Proposal: keep DEKs in a separate key table whose backup retention equals the erasure window. Needs counsel.

**Offsite store.** Operator's choice behind `IBackupTarget`. Example: restic to S3-compatible storage or Backblaze B2 (2024-dated price, unverified for 2026). Restic's encryption is secondary-sourced.

**Instance export and migration.** Three artifacts, not interchangeable:

| Artifact | Contents | Use |
|---|---|---|
| Backup | Everything above, encrypted | Disaster recovery |
| Soul export (`07`) | Charter, preferences, skills; no Passport, memory, reputation or VM disks | Sharing one Soul |
| Instance bundle | Base backup and WAL to cutover, Vault bundle, Soul folders, golden parent references, sealed KEK, config without secret values, SHA-256 manifest | Moving to a new host |

Cutover: (1) drain turns (`03` shutdown) and stop VMs; (2) take a final base backup and verify it; (3) restore read-only on the new host and run drill checks; (4) fence the old instance by revoking GitHub App tokens and Passport leases and blocking egress; (5) switch; keep old disks until the drill passes. The advisory lock in `03` is per database and does not fence two hosts.

**Behaviour and edge cases.**
- An archive gap or missed base backup raises an alert and turns the dashboard red.
- A full archive volume blocks WAL removal, so disk use is a monitored input (warn 80%, red 95%).
- Backup repositories hold Passport ciphertext and private memory. Only the owner runs restores.
- Soul folders are snapshotted at a turn checkpoint, so the marker reflects a quiet point.

## Testing
- Real PostgreSQL 18: base backup, write rows, archive, point-in-time restore to a timestamp; GC row counts and max seq per GC match.
- Archiver: reports failure when the target is full or unwritable; PostgreSQL keeps the WAL.
- Restore refuses a non-empty target. Rollback writes a recovery entry and `next_seq` clears the highest seen; clients get `stale_cursor`.
- Erasure: after key expiry, an old snapshot's ciphertext is unreadable; before it, the gap is asserted and documented.
- KEK export round trip; wrong passphrase rejected; real DPAPI on the NUC (`Category=Windows-host`).
- Instance bundle digest mismatch refused; old instance holds no leases after cutover.
- Avalonia.Headless: stale RPO shows red.

**Drill checklist (quarterly).**
1. Restore latest base plus WAL to a scratch host; counts match at a fixed timestamp.
2. Newest offsite WAL age is within RPO.
3. `pg_restore` a dump; run a pgvector similarity query.
4. Recreate one agent VM from the golden parent; reattach its Soul folder; start it.
5. Decrypt one Passport item using the offline KEK copy, not the server's.
6. Record elapsed times and byte sizes.
7. Confirm restore works with offsite credentials alone and with the repository password on a clean machine.

## Work packages
1. Backup contracts and status records in `Bazaar.Contracts`; `IBackupTarget`, `IBackupEngine`. Depends on `03`.
2. Archiver helper with fsync and verify; real-Postgres tests. Depends on 1.
3. Base backup job, retention and status. Depends on 1 and `04` WP2.
4. Offsite shipper: restic adapter, fake target. Depends on 3.
5. Vault remote push and bundle. Depends on `18` writer.
6. Soul folder snapshot with consistency marker. Depends on `16`.
7. KEK sealed export and import; DPAPI provider. Depends on `14` WP3.
8. Restore runner: empty target, PITR target. Depends on 3, 4.
9. Rollback recovery entry and sequence floor in the GC writer. Depends on `04` WP4.
10. Separate DEK table with window expiry. Depends on `08` §9, `14` WP4.
11. Instance bundle export and import; cutover fencing. Depends on 5, 6, 7, 8.
12. Backup status card. Depends on 3 and `27`.
13. Drill runbook (documentation only). No code dependency.

## Conflicts
1. **DPAPI machine scope vs new-host recovery.** `14` binds the KEK to the host, which protects a copied disk. Migration and rebuild on new hardware need the KEK elsewhere. A sealed export creates a second copy of the master key. Owner decision.
2. **Erasure vs backups and escrowed KEK.** `08` and `14` destroy keys; snapshots and the escrow can still recover them until expiry. Window: 35 days in `04` Q7, "until expiry" in `08`. Also conflicts with the never-purged rule (`14` C4).
3. **Rollback vs append-only and gap-free sequence.** `04` treats an older restore as an incident; `01` and `02` require gap-free sequence. The policy above adds a documented exception to `01`.
4. **Windows backup unverified (largest risk).** `pg_basebackup` and `archive_command` under the service account are unverified; the PostgreSQL Windows page returned 404; pgBackRest and WAL-G are not Windows-supported. Hyper-V checkpoint and export behaviour is unverified (Microsoft pages returned 404).
5. **Soul Marketplace status.** `Bazaar.md` calls its nature DECIDED; `Bazaar.Soul.md` and `07` say PROPOSED. The Soul export is needed only if it is in v1.
6. **Third-party copies of personal data.** Offsite stores hold encrypted Passport and private memory. Processor terms and cross-border transfer need counsel for multi-person installs (`Bazaar.Server.md` §17).
7. **Soul mount count.** `16` Q2 may add a second mount; snapshot scope depends on the answer.

## Questions for the owner
1. Offsite tool and store. Default: restic to any S3-compatible bucket; B2 only after its price is re-checked.
2. WAL shipping. Default: `archive_timeout` 15 min; offsite RPO target 1 hour.
3. Backup retention. Default: two base sets plus WAL; restic keeps 14 daily and 8 weekly snapshots.
4. KEK portability (Conflict 1). Default: passphrase-sealed export for migration; DPAPI stays the everyday provider.
5. KEK holders. Default: one sealed copy held by you; optional second holder named by you.
6. Erasure backup window. Default: 35 days; DEKs in a separate key table.
7. Rollback policy. Default: point-in-time recovery to the last recoverable point; unrecoverable ranges recorded, never reconstructed.
8. Golden parent backups. Default: offsite copy of each referenced parent; recipe rebuild as fallback.
9. Child VM disks. Default: not backed up; enforce push before rebase.
10. Migration credentials. Default: move the KEK by sealed export, then rotate GitHub App keys.
11. Audit head copies. Default: also in the offsite store under its own prefix.
12. Drill cadence. Default: full drill quarterly; WAL-age check daily.
13. Backup volume thresholds. Default: warn at 80%, red at 95%.
14. Soul Marketplace in v1? Default: no; the format is designed only (`07` Q2).
