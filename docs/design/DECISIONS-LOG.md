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

## Round 2 (2026-10-25)

| Register | Decision | Effect on the docs |
|---|---|---|
| C01 Erasure | **RESOLVED**: personal fields are stored as per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone; structure, authorship and timestamps stay. Legal sufficiency still needs counsel. | The first migration (doc 04) needs a key table and encryptable `body`/personal fields; docs 01, 06, 08, 10, 22, 26, 32, 33 adopt it. Backup retention must equal the erasure window (doc 33). |
| C02 Mounted folder | **RESOLVED**: the per-agent folder holds metadata **plus a session-cookie profile**, detached automatically when the session is hard-tainted (doc 15's design). | This overrides the research and the recommended option (metadata only), so the owner has accepted the cookie-profile risk. Doc 14 changes: cookies are mountable, not only brokered; canary credentials and a separate share and OS account per agent (doc 15) become required. Doc 16 Q2's second share stays an explicit exception. |
| C05 Secret custody | **RESOLVED**: the Identity Service holds provider keys and GitHub App keys from the start; one server-side `ISecretStore` owned by doc 14; git pushes run host-side so tokens never enter a VM. | Docs 03 and 05 reduce their resolvers to bootstrap (KEK and database credential); 17 and 21 change pushes to host-side; 27 renames its client-side store interface. Windows KEK custody vs new-host recovery (doc 33 C1) is still open. |
| C06 Taint scope | **RESOLVED**: external content taints hard; agent text taints softly and propagates only from a tainted source session; Tier 2 actions are gated on hard taint; a fresh-session path with an approved summary exists. | Docs 12, 14, 15, 17, 21, 22, 36 align to this. Doc 36's "autonomous reply" still cannot work in the session that read the thread; a fresh-session reply path is needed. |
