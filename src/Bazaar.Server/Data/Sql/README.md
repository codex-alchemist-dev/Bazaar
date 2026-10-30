# Migrations

Embedded, versioned SQL. Files are named `common/NNNN_name.sql` (or `windows/` and `linux/` for provider-specific
ones). One owner per number; never reuse or renumber a number, and never edit an applied file (the runner refuses).
The runner applies any not-yet-applied file in numeric order, even below a number already applied, so PRs may merge in any order.

## Registry

| Number | Owner | Status | Content |
|---|---|---|---|
| `0001` | G1 | merged | Core GC schema: `gc`, `gc_member`, `gc_event` (hash-partitioned), `gc_message`, `gc_message_version`, `gc_mention`, `schema_setting`, roles |
| `0002` | GW3 | PR #66 | Gateway tables (no foreign keys to GC tables) |
| `0003` | ST1 | PR #74 | `setting` table |
| `0004` | A3 | PR #69 | Audit table, app role cannot UPDATE |
| `0005` | MAIN | reserved | Cross-table foreign keys |
| `0006` | JOB1 | taken | Jobs |
| `0007` | search | PR #73 | FTS `search_document` |
| `0008` | G1b | this PR | `entry_ref`, `read_state`, `consumer_cursor`, `gc_access_request`, `person_key` and `erase_person_key`, `embedding_profile`; `gc_member` becomes insert-only with key `(gc_id, principal_id, joined_seq)` |
| `0009` | VLT4 | merged | Vault index: `vault_docs`, `vault_blocks`, `vault_block_embedding` (halfvec, dimension checked against `embedding_profile`), `vault_links`, `vault_commit`, `vault_state`; enables `vector` |
| `0010` | AR1 | PR #97 | Agent runtime: `agent`, `inbox_item`, `turn`, `turn_step`, `agent_lease` (doc 06 WP1; no cross-table foreign keys) |
| `0011` | APR1 | this PR | approval table (doc 15) |
| `0012` | KB1 | merged | Kanban: `tasks`, `task_edges`, `task_links`, `task_events` (append-only); `bazaar_app` gets column-level UPDATE on `tasks` |
| `0014` | SOUL1 | merged | `soul_versions`: append-only Soul version chain (charter SHA-256, preferences JSON, expected-version writes) |
| `0015` | INT1 | merged | `interrupt_frame`: per-agent interrupt stack (doc 24 WP5); no foreign keys to agent or GC tables |
| `0016` | SKM1 | this PR | Skimmer gate: `gate_config`, `wake_decisions`, `gate_labels` (doc 13; no `gate_run_override`, which waits on question 7); no cross-table foreign keys beyond `agent` and `gc_event` |
| `0017` | CAL1 | this PR | `calendar_event`: Company Calendar (doc 24 section 4); title stored as `(title_key_id, title_ct, title_nonce)`; `agent_id` references `agent`, no other cross-table foreign keys beyond `gc` |
| `0018` | ORG1 | this PR | `org_relation`: org edges (`reports_to`, `lent_to`), live-edge and one-`reports_to` unique indexes, cycle and read-only trigger; no foreign keys to agent or project tables |
| `0013` | REP1 | this PR | reputation ledger tables: `review`, `outcome_event`, `outcome_attribution`, `reviewer_calibration`, `weights_version`, `score_snapshot`, `reputation_event` (append-only) |
| `0019` | EMAIL1 | this PR | Mail core (doc 22 tables): `mail_domain`, `mail_mailbox`, `mail_message`, `mail_outbound`, `mail_blob`; personal bodies encrypted per person |
| `0020+` | free | | Claim one by adding a row here in your PR, or ask the main session |

## Claiming a number

1. Take the lowest free number and add your row (owner, work package, one-line content) to this table in the same PR as the migration.
2. If two PRs claim the same number, the one merged second renumbers before merging; the table is the tie-breaker.
3. Gaps are harmless: a late file below the highest applied number still runs, in numeric order. The runner still refuses duplicate versions and edited applied files.

Source: `docs/implementation-plan.md` and the development plan section 4.
