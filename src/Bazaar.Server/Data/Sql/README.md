# Migrations

Embedded, versioned SQL. Files are named `common/NNNN_name.sql` (or `windows/` and `linux/` for provider-specific
ones). One owner per number; never reuse or renumber a number, and never edit an applied file (the runner refuses).

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
| `0012` | KB1 | this PR | Kanban: `tasks`, `task_edges`, `task_links`, `task_events` (append-only); `bazaar_app` gets column-level UPDATE on `tasks` |
| `0014` | SOUL1 | this PR | `soul_versions`: append-only Soul version chain (charter SHA-256, preferences JSON, expected-version writes) |
| `0015` | INT1 | this PR | `interrupt_frame`: per-agent interrupt stack (doc 24 WP5); no foreign keys to agent or GC tables |
| `0009-0011`, `0013`, `0016+` | free | | Claim one by adding a row here in your PR, or ask the main session |

## Claiming a number

1. Take the lowest free number and add your row (owner, work package, one-line content) to this table in the same PR as the migration.
2. If two PRs claim the same number, the one merged second renumbers before merging; the table is the tie-breaker.
3. Do not skip numbers except the reserved `0005`; the runner refuses out-of-order and duplicate versions.

Source: `docs/implementation-plan.md` and the development plan section 4.
