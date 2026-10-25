# Design decisions log

Owner answers to the cross-cutting conflicts in `CONFLICT-REGISTER.md`, in the order given. Each entry says what must change in the design docs; a reconciliation pass applies them. Status: **RESOLVED** (owner decided) or **FOLLOW-UP** (a detail still needs an answer).

## Round 1 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C03 VM topology | **RESOLVED** (earlier, owner 2026-10-24): every agent gets a Windows VM and a Linux VM. Spike S8 is for sizing only. | CLAUDE.md "Agent VMs" line already says so. Doc 16 Q13 and 35 Q4 follow it. |
| C09 GC log shape | **RESOLVED**: separate `gc_event` + `gc_message` + `gc_message_version` tables (doc 04's model), full copy per version. | Docs 01, 02, 15 and 22 must be redrafted to this model (they assumed one `entry` table with edits as entries). Trust columns and external-mail kinds need a home in 04's tables. |
| C10 Partitioning | **RESOLVED**: hash partitions from day 1 (by `gc_id`). | Doc 04 and Server.md §3 updated (no monthly partitioning). **FOLLOW-UP:** the partition count must be chosen before the first migration; restore policy (sequence floor + recovery entry, doc 33) is still PROPOSED, not yet confirmed. |
| C04 Runtime table ownership | **RESOLVED**: doc 06 owns agent, turn, step, lease and `inbox_item`; 07 owns Soul versions; 09 owns schedule and availability; 05 owns quota and model policy; 23 owns activity. Others add kinds, not tables. | Docs 03, 04, 07, 09, 13, 23, 24 drop their duplicate definitions. |
| C07 Pub/sub | **RESOLVED**: Postgres NOTIFY for GC fan-out; Valkey (Linux) / Garnet (Windows) for cache and presence. | Garnet stays in the first Windows host's dependencies, so spike S2 (StackExchange.Redis compatibility and persistence on Windows) gates the presence/cache work. Docs 02/03/04 keep NOTIFY for GCs; 11 and 23 may use the cache provider. |
