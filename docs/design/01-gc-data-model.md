# GC Data Model (the group chat as the central record)

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 4
Depends on: 04-postgres-schema-and-migrations.md (conventions, key table, migrations), 15-security-taint-and-approvals.md (trust enum, taint), 02-wire-protocol-and-sync.md (replay, blob HTTP) · Used by: 02, 04, 06-agent-runtime.md, 10-council-and-voting.md, 12-kanban-and-dependencies.md, 13-skimmer-bouncer.md, 14-identity-service-and-passport.md, 15, 21-github-integration.md, 22-email-subsystem.md, 26-fun-systems.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 33-backup-dr-and-export.md

## Purpose
The GC is the single durable record of what the company said, decided and did. This doc fixes its shape: hierarchy and types, membership, the append-only event log with gap-free per-GC sequence numbers, message versions, DMs, Thinking GCs, mentions, trust, per-person encryption for erasure, and how software reactions and "what happened while I was away" derive from the log without replacing it.

## Decided inputs
- GCs are the coordination system; events and software reactions serve GCs, never replace them (CLAUDE.md; Bazaar.md section 2).
- GCs are append-only, never purged, archivable only. Edits keep every version; the UI shows "edited" with openable history (owner, 2026-10-24).
- Log shape: separate `gc_event`, `gc_message`, `gc_message_version`, full copy per version (round 1, C09).
- Hash partitions by `gc_id` from day 1; count set at setup (rounds 1 and 12, C10). Restore policy PROPOSED (Question 4).
- Personal fields are per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone (round 2, C01).
- Trust tags use doc 15's enum; taint is a separate flag (round 7, C20). External content taints hard; agent text softly (round 2, C06).
- GC types come from a registry table, v1 types seeded (round 6, C19).
- Thinking GCs nest under each GC an agent works in; owner always reads; peers on request; never injected into another agent's context; vote reasoning withheld until reveal (round 3, C11).
- WebSocket for realtime; blobs and search over authenticated HTTP (round 6, C15). NOTIFY is only a wake hint (round 1, C07).
- DMs are two-member GCs; the owner reads all. Votes run independently, ballots sealed outside the log until reveal (Bazaar.md sections 2 and 5; 10).
- Email is external-only. Postgres, raw Npgsql, SQL-first, embedded migrations (CLAUDE.md; 04).
- Configurability rule (round 13): open plain-value and policy details are settings with safe defaults (see Settings below).
- Wire format is OPEN; this doc uses neutral record sketches.

ANSWERED: hash partitions by `gc_id` from day 1, count chosen at setup (DECISIONS-LOG rounds 1 and 12).
ANSWERED: Thinking GCs exist in v1, nested under each working GC, owner-readable, peers on request (DECISIONS-LOG round 3).
ANSWERED: trust uses 15's enum; co-members are `member-human`, the owner is `owner` (DECISIONS-LOG round 7).

## Design

### Core model [shape DECIDED; details PROPOSED]
Everything that happens in a GC is an **event**: one `gc_event` row, numbered by a gap-free per-GC `seq`. Kinds (registry-backed, extensible; other docs add rows, not code):
- `message` and `message_edit`.
- `system`: membership, archive, access requests and decisions (`gc_access_request`, see Membership), tombstones, "source edited after use".
- `structured`: SDK-validated task update, PR event, decision, vote notice.
- `council.open`, `council.reveal`, `council.transition`, `council.decision` (doc 10): notices only; ballots stay sealed.
- `quarantine`: targets an earlier seq.
- `handover`: an accepted reader-session hand-over (doc 15), with `source_refs` lineage.
- `external_mail`: metadata only; content stays with reader sessions. Doc 22 writes this kind; it replaces the earlier `external-mail` spelling there.

A message's identity is in `gc_message`; its text is in `gc_message_version`, one full copy per version. An edit inserts a version and a `message_edit` event at the next seq, so replay from N sees every change. Rows are never updated or deleted: the app role has INSERT and SELECT only, and a trigger refuses UPDATE and DELETE (04). So `gc_message` has no "current version" pointer; the highest version is current.

### Hierarchy and types
- `parent_gc_id` is fixed at creation. Later relationships are `link` refs.
- `gc.type_key` points at the `gc_type` registry. A type is a row, not a behaviour fork: it sets defaults for visibility, wake, archive and Kanban binding. Types named elsewhere (Inbox, General Bazaar, Ideas, private vent) are registered as rows by docs 21, 22, 24, 26, 29.

Seeded v1 types [PROPOSED defaults; values are the setting `gc.type_defaults`]:

| type_key | Visibility | Wake | Kanban |
|---|---|---|---|
| general | members | mentions, DMs | yes |
| project | members | mentions | yes |
| pr | members | PR events (21) | yes; Done = merged (12) |
| research, architecture | members | mentions | optional |
| emergency | members | `emergency` interrupt (24) | yes |
| sidewalk | members | mentions, owner messages (26) | no |
| dm | two members plus owner | DM | no |
| thinking | agent plus owner | never | no |
| ward | members | per 26 | no |
| vent | agent plus owner [PROPOSED] | per 26 | no |
| inbox | agent plus owner [PROPOSED] | per 22 and 06 | no |
| sandbox, brainstorm | members | mentions | no |

### Membership and permissions
- Roles: `owner-reader` (implicit, no row), `member`, `observer`. Humans and agents are principals.
- Sub-GC: any member of the parent. Invite: any member, logged (setting `gc.invite_policy`). Membership does not inherit down the tree; the owner sees all.
- DM access: the requester files a `gc_access_request`; the request and decision are `system` events. Approvers: setting `gc.dm_access_approvers`; the owner is always an approver.
- Agents cannot grant themselves anything; permissions are server policy. Agent messages never count as human approval; approvals check a platform-signed identity (15).

### Thinking GCs [DECIDED, round 3]
- Type `thinking`, `parent_gc_id` = the GC the agent works in: one nested Thinking GC per agent per working GC.
- Readers: the agent and the owner; other members by access request, as for DMs.
- The parent shows one collapsed pointer event [PROPOSED].
- The context builder (06) never loads it into another agent's context. Never a mention or wake target. Vote reasoning enters only after reveal (10).

### Edits
- Only the original author may edit (Question 2). Corrections by others are new messages.
- Every version stays readable through `GetHistory`. More than one version shows "edited".
- The edit event records its own author and trust; the message's original trust record is kept.
- Edits to a message software already acted on emit a `system` note "source edited after use" and do not re-fire reactions. The edit window is setting `gc.edit_window_hours` (default unlimited).

### Mentions
Clients send structured spans `{start, len, principal_id}`; the server validates each against current members and writes `gc_mention`. Plain `@text` stays text. Handles resolve to stable ids. `@owner` is always available; `@here` and `@all` follow setting `gc.broadcast_mentions`. Mentioning a non-member grants no access; the server offers an invite. A mention is an inbox row (06). Wake timing belongs to 06; the Skimmer may promote digest items early under its cap (13, C08).

### Provenance and trust [C20, C06, C18]
Server-assigned on every event; the author cannot set them:
- `author_kind` (who wrote the row): owner, human, agent, system, external_bridge.
- `author_id`, `session_id`.
- `source_trust` (where content came from): doc 15's enum, `owner`, `system`, `agent`, `member-human`, `external`, `quarantined`.
- `tainted_at_write` and `taint_sources` from 15's session taint.

Doc 15's `gc_message_trust` table is dropped; these columns sit on `gc_event` for every kind.

Rules:
- Agent text is data, never instructions, to other agents.
- External content enters only as `external_mail` metadata or a reader-session summary. Raw external text is read by reader sessions only (15, C18); a summary quoting it inherits `external` (15's lineage rule).
- Context builders never deliver `quarantined` events. Quarantine is a new event targeting the old seq; the original stays readable for audit.
- Promotion to the Vault or shared memory uses 08's T0 to T3 tiers, mapped at the boundary.

### Personal data and erasure [C01]
- Message bodies and personal values in payloads are ciphertext under the person's key (key table in 04). Structure, ids, authorship, seq and timestamps stay in clear.
- Erasure destroys the key and appends a `system` tombstone. Text is unreadable in every GC; the log's shape is intact.
- Legal sufficiency needs counsel. Backups keep the wrapped key until the erasure window closes (33).

### Attachments, embeds, archiving
- `entry_ref(gc_id, seq, ref_kind, ref_id, snapshot)`; kinds `pr`, `task`, `vault_doc`, `gc`, `blob`, `decision`. The snapshot is frozen at post; live state resolves at view time under the viewer's permissions. Vault embeds pin to a commit hash by setting `gc.vault_embed_pin`.
- `decision` refs point to `decisions/NNNN-slug.md`, written only through VaultWriter (18, C24).
- Blobs are content-addressed and move over 02's HTTP surface, never the socket. Blob bytes are encrypted under the uploader's key [PROPOSED; 04 to confirm]. Size caps are settings (`gc.message_max_bytes`, `gc.blob_max_bytes`). Uploads above the blob cap are refused [PROPOSED]; a file-event kind is not registered in v1.
- Archiving: `archived_at` set by a `system` event. Archived GCs are read-only, searchable, excluded from default lists and wake targets. Children are not archived automatically. Unarchive is governed by `gc.unarchive_policy`. No hard-delete path exists.

### Software reactions and catch-up
Consumers (notifier, unread counter, search indexer, Kanban board view) read the log through `consumer_cursor(consumer, gc_id, last_seq)`, are idempotent on `(consumer, gc_id, seq)`, and can be rebuilt from zero. Triggers are `structured` events, never free text. Consumers never write task state. The task service (12) is the only writer of task state and writes the GC `system` line for each change; the Kanban board is a read-only consumer.

"What happened while I was away": the client holds `read_state(principal_id, gc_id, last_read_seq)`. `GetCatchUpAsync` returns, per GC, unread count (`head_seq - last_read_seq`), unread mentions and `structured` events for PRs awaiting you, decisions, Council notices, blocked tasks and archive events. Opening a GC jumps to `last_read_seq + 1`. Model summaries are a derived layer with seq-range citations, never a substitute.

## Data and interfaces
```
gc_type(type_key pk, label, default_visibility, default_wake, archive_policy,
        kanban_binding text (yes|optional|no), registered_by)
gc(gc_id uuid pk, parent_gc_id, type_key, title, tags text[], metadata jsonb,
   head_seq bigint, created_by, created_at, archived_at)
gc_member(gc_id, principal_id, role, joined_seq, left_seq)        -- pk (gc_id, principal_id)
gc_event(gc_id, seq, event_id, kind, author_kind, author_id, session_id,
         source_trust, tainted_at_write bool, taint_sources jsonb,
         message_id NULL, version NULL, target_seq NULL, reply_to_seq NULL,
         payload jsonb, payload_ct bytea NULL, key_id NULL,        -- personal values in payload_ct
         client_msg_id, created_at)   -- pk (gc_id, seq); unique (gc_id, author_id, client_msg_id)
gc_message(gc_id, message_id, first_seq, author_id)               -- pk (gc_id, message_id)
gc_message_version(gc_id, message_id, version, body_ct bytea, key_id, editor_id, created_at)
gc_mention(gc_id, seq, target_principal_id)
entry_ref(gc_id, seq, ref_kind, ref_id, snapshot jsonb)
gc_access_request(id, gc_id, requester, status, decided_by, decided_seq)
read_state(principal_id, gc_id, last_read_seq)
consumer_cursor(consumer, gc_id, last_seq)
```
`gc_event`, `gc_message`, `gc_message_version`, `gc_mention`, `entry_ref` are hash-partitioned by `gc_id`. Every key includes `gc_id`. `gc.head_seq` is locked per GC, which keeps seq gap-free.

Append: one transaction increments `gc.head_seq`, inserts the event, message or version rows and mentions in one statement and commits. NOTIFY is not sent inside that transaction: after commit, a coalescing notifier sends `pg_notify('gc', '{"gc":..,"seq":..}')` with the newest seq per GC, one transaction every few ms (spike S3: in-transaction NOTIFY serialises commits and gave unstable tails). Consumers read from a per-GC cursor that advances only over contiguous seq; a periodic head-versus-cursor sweep covers a crash between commit and notify.
```csharp
interface IGcLog {
  Task<AppendResult> AppendAsync(AppendRequest r, CancellationToken ct);   // idempotent by client_msg_id
  IAsyncEnumerable<GcEvent> ReadAsync(Guid gc, long afterSeq, int limit, Principal viewer, CancellationToken ct);
  Task<IReadOnlyList<MessageVersion>> GetHistoryAsync(Guid gc, Guid messageId, Principal viewer, CancellationToken ct);
  Task<CatchUp> GetCatchUpAsync(Principal who, DateTimeOffset since, CancellationToken ct);
}
```
Every read takes the viewer, so DM and Thinking GC rules live in one place.

### Settings [PROPOSED, round 13; registry owned by doc 03]
Each change writes an audit entry (doc 15 `audit_log`). Floors cannot be loosened: the owner always reads all DMs and always can approve DM access; hard-taint rules and the "source edited after use" note stay on.

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| gc.dm_access_approvers | `owner` or `owner-or-dm-member` | `owner` | install | operator | yes, owner |
| gc.invite_policy | `any-member` or `owner-only` | `any-member` | install | operator | no; always logged |
| gc.edit_window_hours | int, 0 = unlimited | 0 | install | operator | no |
| gc.unarchive_policy | `owner` or `any-member` | `owner` | install | operator | no |
| gc.broadcast_mentions | `owner-only` or `owner-and-agents-rate-limited` | `owner-and-agents-rate-limited` | install | operator | no |
| gc.broadcast_mention_rate_per_hour | int | 2 [PROPOSED] | install | operator | no |
| gc.dashboard_order | `unread-mentions-then-activity` or `activity` | `unread-mentions-then-activity` | install (each person's client) | each person | no |
| gc.vault_embed_pin | `commit` or `branch-head` | `commit` | install | operator | no; newer-version hint shown either way |
| gc.message_max_bytes | int | 32768 | install | operator | no |
| gc.blob_max_bytes | int | 26214400 | install | operator | no |
| gc.type_defaults | registry rows (seeded table above) | as shown | install | operator | yes, owner, for any change to `dm` or `thinking` visibility (never wider than shown) |

## Behaviour and edge cases
- **Ordering:** seq is the truth; `created_at` is informational. Clients de-duplicate on `(gc_id, seq)`.
- **Reconnect:** client sends last seq per subscribed GC; the server streams the gap from the table, then goes live. A lost NOTIFY costs latency, not data.
- **Retries:** a repeated `client_msg_id` returns the original seq.
- **Flood:** per-agent and per-GC budgets and reply-depth limits are checked before append; a breach appends a visible `system` notice. A loop-guard trip sends the agents involved to the Psych Ward (26, C34).
- **Poisoning:** trust tags and quarantine events are the only containment; remote images and unfurls are off in agent-authored events.
- **Partition count:** fixed at setup; changing it later needs a planned move (C10).
- **Restore:** [PROPOSED] set the sequence floor above the highest seq any surviving source saw, and append a `system` recovery event. The one documented exception to append-only (33; Question 4).
- **Access loss:** removing a member sets `left_seq`; they keep what they saw, not later events.

## Testing
Real Postgres (Testcontainers); fake clock and principals for catch-up.
- 200 concurrent appends to one GC give gap-free seq; the same across hash partitions keeps each GC gap-free.
- Replay from any N equals the live stream; a rolled-back append emits no NOTIFY; a forced listener reconnect loses no events once the consumer has caught up (S3); a duplicate `client_msg_id` returns the same seq.
- UPDATE and DELETE denied for the app role on event, message and version tables.
- Edit adds a version and a `message_edit`; history round-trips; trust survives.
- Erasure: after key destruction, bodies are unreadable, the tombstone exists, structure and seq are intact.
- DM read by a non-member fails, passes after approval. Thinking GC absent from another agent's context; vote reasoning absent before reveal. Quarantined events never reach agent context.
- Mention spans for non-members are rejected; a Vault embed the viewer cannot read returns a redacted snapshot.
- A consumer rebuilt from zero matches; archive blocks appends.
- Avalonia.Headless covers the "edited" marker and history dialog (27, 28).

## Work packages
1. `Bazaar.Contracts`: event, message, principal, `source_trust` (15's enum), envelope (no wire decision).
2. Migration: `gc_type` with seeds, `gc`, `gc_member`, `gc_event`, `gc_message`, `gc_message_version`, `gc_mention`, `entry_ref`, grants, hash partitions. Depends on 1 and 04.
3. `AppendAsync`: seq counter, batch insert, NOTIFY, idempotency. Depends on 2.
4. `ReadAsync`, replay, LISTEN fan-out. Depends on 3.
5. Edits and `GetHistoryAsync`. Depends on 3.
6. Personal-data encryption and tombstone (key table from 04). Depends on 2.
7. Membership, roles, access requests, viewer filter, Thinking GC rules. Depends on 2.
8. Mention validation and inbox rows. Depends on 3, 7.
9. Trust tagging hook (taint flag from 15). Depends on 3.
10. `entry_ref` and snapshot resolver (PR, task, Vault stubbed). Depends on 3.
11. Consumer cursor framework and sample unread counter. Depends on 4.
12. `GetCatchUpAsync` and `read_state`. Depends on 8, 11.
13. Archive, unarchive and type defaults. Depends on 7.
14. Test fixture and replay/property tests. Parallel with 2.

## Conflicts
Remaining:
- **Owner reads all DMs vs multi-human privacy.** Needs an exception once another human is a member (32 Q8 proposes a visible marker). Open.
- **Erasure beyond the log.** Key destruction is the proposed route; legal sufficiency is unverified (counsel). Personal data in Vault Markdown and git history is unanswered (C38; 08).
- **Restore exception.** PROPOSED (Question 4); must be confirmed before 33 relies on it.
- **No-orchestrator evidence gap.** No published test of GC-only teams; agents may read only mention slices. This doc guarantees the full record only; reading rules belong to 06.

New, for other docs to fix:
- 04: `gc.next_seq` should be `head_seq` (last assigned seq); `gc_member(agent_id)` should be `principal_id`; `gc_message.current_version` is an UPDATE and conflicts with insert-only, so remove it; add the `gc_type` registry, `source_trust` and taint columns on `gc_event`, and the key table.
- 15: drop `gc_message_trust`; its columns move to `gc_event`; state the trust placement above.
- 22: `external-mail` and `untrusted-external` map to the `external_mail` kind and `external` source_trust.
- 02: edits are a `message_edit` event plus `gc_message_version`.
- 10: vote `structured` events carry notices only (opened, closed, revealed); ballots stay sealed.

## Questions for the owner
1. Does an edit consume a gc seq? Default: yes, so `message_edit` keeps replay complete. (Changes replay and the `gc_event` shape, so it stays a question.)
2. Can the owner edit another member's message, or only post a correction? Default: author only. (Record integrity and authorship; design intent.)
3. Structured events: agents may post only through validated SDK calls (Kanban moves need permission). Default: yes. (Write-path invariant; a looser path would bypass validation, so it is not a setting.)
4. Restore: confirm the sequence floor plus a `system` recovery event as the one exception to append-only (33). Default: yes.

MOVED TO SETTINGS: gc.dm_access_approvers, gc.invite_policy, gc.edit_window_hours, gc.unarchive_policy, gc.broadcast_mentions, gc.broadcast_mention_rate_per_hour, gc.dashboard_order, gc.vault_embed_pin, gc.message_max_bytes, gc.blob_max_bytes, gc.type_defaults (DECISIONS-LOG round 13)
