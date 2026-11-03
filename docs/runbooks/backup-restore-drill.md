# Backup and restore drill (runbook)

Source: `docs/design/33-backup-dr-and-export.md`, drill checklist and WP13. Run it quarterly (`backup.drill_interval_days`, default 90) and after any change to the backup engine, the archiver or PostgreSQL major version. Only the owner runs restores.

This runbook covers the PostgreSQL base-plus-WAL path. The offsite copy, the Vault remote push and the Soul snapshot are checked separately (doc 33 checklist items 2 and 4).

## Before you start

- Use a scratch host or a scratch data directory. Never restore over a live data root; the restore runner refuses a non-empty target.
- Have a base set and its `bazaar-backup-set.json` sidecar from `BaseBackupJob`.
- Have the WAL archive directory that `archive_command` filled.
- Pick a UTC recovery target time and record it before the drill starts.

## Steps

1. **Check the base set.** Run `pg_verifybackup <set>`. It must print no errors.
2. **Check the major version.** `PG_VERSION` in the set must equal the server's expected major (`RestoreSchemaMismatchException` otherwise).
3. **Restore into an empty directory** with `RestoreRunner` (or the same settings by hand):
   - `restore_command` fetches archived WAL from the archive directory.
   - `recovery_target_time = '<UTC target>+00'`.
   - `recovery_target_action = 'promote'`.
   - `archive_mode = 'off'`, so the restored instance never archives into the primary's archive.
   - Create `recovery.signal`. Remove the sidecar from the restored directory.
4. **Start the restored server** on a scratch port. Wait until `select pg_is_in_recovery()` returns `f`. A count taken during recovery is not evidence.
5. **Check the rows.** Counts and the maximum sequence must match the rows committed before the target time: `select count(*), count(distinct i), max(i)`.
6. **Check the offsite copy** (when configured): newest offsite WAL age within `backup.offsite_rpo_target`; restic `snapshots` lists the expected tags.
7. **Record** elapsed times, byte sizes and the target time in the drill log.
8. **Stop** the restored server with `pg_ctl -m fast stop` and delete the scratch directory.

## Recorded drill (2026-10-30, Linux, PostgreSQL 16 in the cloud sandbox)

- Primary: 200 rows; base backup `pg_basebackup -F p -X stream --manifest-checksums=SHA256`; `pg_verifybackup` passed.
- Target time between row 150 and row 151 (UTC).
- Restored and promoted: 150 rows, 150 distinct values, maximum 150, last commit before the target. Pass.
- Gaps: this ran on PostgreSQL 16, not the repo's PostgreSQL 18 target; `archive_command` used `cp`, not the Bazaar archiver; the restic and Windows steps were not run.

## Not yet drilled

- Windows (the owner's PC, `pg_basebackup` and the archiver under the service account). Doc 33 flags this as the largest unverified risk.
- A restore on the owner's PC from offsite credentials alone, with the repository password on a clean machine (checklist item 7).
