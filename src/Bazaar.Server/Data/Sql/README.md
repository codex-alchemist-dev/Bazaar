# Migrations

Embedded, versioned SQL. Files are named `common/NNNN_name.sql`. One owner per number; do not reuse or renumber.

| Number | Owner | Content |
|---|---|---|
| `0001` | G1 | Core GC schema: `gc`, `gc_member`, `gc_event` (hash-partitioned), `gc_message`, `gc_message_version`, `gc_mention`, `schema_setting`, roles |
| `0002` | GW3 | Gateway tables (no foreign keys to GC tables) |
| `0003` | ST1 | `setting` table |
| `0004` | A3 | Audit table, app role cannot UPDATE |
| `0005` | MAIN | Cross-table foreign keys |
| `0006+` | assigned on request | Ask the main session for a number |

Source: `docs/implementation-plan.md` and the development plan section 4.
