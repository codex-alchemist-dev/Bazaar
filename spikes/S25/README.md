# Spike S25: restore drill, Linux half

Throwaway code for `docs/spikes/S25.md`. Synthetic data only. Everything runs in Docker from `pgvector/pgvector:pg18`.

## Question

Can a Postgres 18 base backup plus WAL archive restore the GC log to a chosen timestamp, and does the proposed rollback policy (sequence floor plus a `system` recovery entry, doc 33 and C10) leave no sequence gap and no sequence reuse after the recovery entry?

## Pass criteria (fixed before the first run)

Each run must pass P1 to P4. P5 is measured and reported.

- **P1 Restore matches the target.** At the restore timestamp T, every GC has the same row count, max `seq` and `head_seq` in the restored database as in the source database at T. Restored `pg_last_xact_replay_timestamp()` is at or before T, and no row committed after T is present.
- **P2 Floor and recovery entry.** For each GC, `apply_recovery` writes one `system` entry at `seen_head + 1`, where `seen_head` is the highest `seq` the surviving source saw. Its body records `lost_from` (restored head + 1) and `lost_to` (`seen_head`). `head_seq` equals the entry's `seq`.
- **P3 No gap after the recovery entry.** No row exists in `[lost_from, lost_to]`. Rows from `lost_from - 1` down to 1 are contiguous. Rows from the entry onward are contiguous.
- **P4 No reuse.** The first message appended after recovery gets `seen_head + 2` in every GC.
- **P5 Measured, reported against the proposed budgets (doc 33):** base backup time, archive lag (time from the last source commit to the WAL segment being archived), restore time (container start to promotion), and database size. Against RPO 1 h, archive lag must be under 1 h. Against RTO 1 d, the restore time is reported as a lower bound only, because this is synthetic data and not a full owner's PC rebuild.

**Fail consequence.** Any failed P1 to P4 in any run fails the spike. The DECISIONS-LOG entry for the restore policy then stays FOLLOW-UP and the policy is revised. A P5 miss is reported, not a fail on its own.

## Time-box

2 days (doc 35). Hard stop.

## Runs

Three runs, each with fresh containers and a fresh data directory. Report the median and the spread.

## How to run

```
spikes/S25/run_drill.sh <run-number> <work-dir>
```

`<work-dir>` must be outside the repo. It holds the base backup, the WAL archive and the restore target.
