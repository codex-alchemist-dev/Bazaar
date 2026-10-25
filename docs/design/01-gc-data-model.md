# GC Data Model (the group chat as the central record)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: none (this is the base record) · Used by: agent runtime / work-life design, Kanban design, Council design, taint and approval design, Identity Service design, client GC view and dashboard design, Skimmer Bouncer design, backup and erasure design

## Purpose
The GC is the single durable record of what the company said, decided and did. This doc fixes its shape: hierarchy, membership, the append-only entry log with per-GC sequence numbers, edits, DMs, mentions, trust tags, attachments, archiving, and how software reactions and "what happened while I was away" are derived from the log without replacing it.

## Decided inputs
- GCs are the coordination system; events and software reactions serve GCs, never replace them (CLAUDE.md; Bazaar.md section 2).
- No prescribed structure: coworkers create, nest and group GCs; Project, PR, Research and Architecture GCs grow organically (Bazaar.md section 2).
- GCs are append-only and never purged; archivable only. Edits are allowed, every version is kept, UI shows an "edited" marker with openable history (owner, 2026-10-24).
- DMs are two-member GCs; the owner reads all; other members may request access, details OPEN (owner, 2026-10-24).
- Envelope with per-GC sequence numbers and replay-from-seq; append plus `NOTIFY {gc_id, seq}` in one transaction; one `LISTEN` connection per process (Bazaar.Server.md; implementation-plan.md).
- Postgres, raw Npgsql, SQL-first repositories, embedded versioned SQL migrations; migrations only add to GC tables (CLAUDE.md; data_access.md).
- Taint tracking plus human gate for high-impact actions after untrusted input (Bazaar.Server.md Open Question 13).
- Email is external-only; internal coordination stays in GCs.
- Each agent has its own timezone and schedule (work-life simulation).
- Wire format (shared C# contracts vs Protobuf) is OPEN: this doc uses neutral record sketches only.

## Design

### Core model [PROPOSED unless noted]
Everything that happens in a GC is an **entry**: one row in an append-only log, numbered by a gap-free per-GC `seq`. Entry kinds: `message`, `edit` (points at an earlier seq), `system` (member joined, GC archived, access granted), `structured` (typed event posted through the SDK: task update, PR event, decision, vote), `quarantine` (marks a target entry). Edits, moderation and membership changes are entries too, so a client that replays from seq N sees every change and never needs a second channel. Rows are never UPDATEd or DELETEd; the app role has INSERT and SELECT only on the entry tables.

### Hierarchy
- A GC has an optional `parent_gc_id`. The parent is fixed at creation (no re-parenting); a later relationship is expressed as a `link` attachment. This keeps the tree and any materialized path stable.
- Types (all PROPOSED, metadata concept approved but not spec'd): `general`, `project`, `pr`, `research`, `architecture`, `emergency`, `sidewalk`, `dm`, `thinking`. Type is a label with defaults (visibility, archive policy), not a behaviour fork. Free-form `tags` and a `metadata` JSON object sit beside it.
- **Thinking GC** (new term, not in any existing doc): a GC nested under the GC an agent is working for, holding that agent's visible deliberation. Proposed rules: one agent member plus the owner reading; other members may request access as for DMs; the parent shows a collapsed pointer entry, not the content; Thinking GCs are never mentions-wake targets. Cost and noise are the open concerns (Q5).

### Membership and permissions
- Roles per GC: `owner-reader` (the boss; implicit in every GC, not a row), `member`, `observer` (read only). Humans and agents are both "principals". Agents cannot grant themselves anything; permissions are server policy, not prompt.
- Create a sub-GC: any member of the parent. Invite: any member of the GC (Q3). Membership does **not** inherit down the tree; the owner sees all.
- DM = `type=dm`, exactly two members. A third reader needs an **access request**: `gc_access_request(gc_id, requester, status, decided_by)`, a `system` entry records the request and the decision. Who may approve is OPEN (Q4). Access is read-forward and read-back by default; the request says which.
- No agent message ever counts as a human approval (threat model, P1 item 11): approvals are checked against a platform-signed human identity, not entry text.

### Edits
`edit` entry = `{target_seq, new_body, editor}`. The current body is the latest edit by an allowed editor; all versions stay readable via `GetHistory(gc_id, seq)`. Allowed editor: the original author only (owner may edit nothing of others' text; Q6). Edited entries carry `edited=true` and `version_count` in the read model. An edit does not change the target's trust tag. Edits to a message already acted on by software do not re-fire reactions; they emit a `system` note "source edited after use".

### Mentions
The sender's client sends structured spans `{start, len, principal_id}` from autocomplete; the server validates each against current members and writes `mention` rows. Plain `@text` with no resolved span stays text. Handles are display names over stable ids, so renames never break old mentions. Special targets: `@owner`, `@here` (members currently on shift), `@all` (all members). Mentioning a non-member does not grant read access; the server appends a `system` entry offering an invite. A mention puts the entry in the target's inbox; whether it wakes the agent now or at next shift follows the work-life schedule, and wake is never dropped, only deferred (consistent with the defer-only Skimmer proposal).

### Provenance and trust
Every entry carries server-assigned fields the author cannot set: `author_kind` (`owner`, `human`, `agent`, `system`, `external_bridge`), `author_id`, `session_id`, `trust`, and `tainted_at_write` (was the author's session tainted when it wrote this). `trust` values [PROPOSED]: `human_verified`, `agent`, `agent_tainted`, `external_untrusted` (email, stranger issue, web bridged in), `system`. Rules: agent-authored text is data, never instructions, for other agents; context builders may drop or summarize `quarantine`d entries; nothing written by a tainted session is promoted to the Vault or shared memory without a human. Quarantine is a new entry targeting the old one, so the original is still readable. Detailed taint mechanics belong to the taint and approval design.

### Attachments and embeds
`entry_ref(gc_id, seq, ref_kind, ref_id, snapshot)`: kinds `pr`, `task`, `vault_doc`, `gc` (link/quote), `blob` (file, content-addressed), `decision`. The snapshot (title, state, hash) is frozen at post time; live state is resolved at view time under the **viewer's** permissions, so an embed never leaks a Vault page the viewer cannot open. Vault docs embed by path plus commit hash, since git is the source of truth.

### Archiving
`archived_at` on the GC, set by an `archive` system entry. Archived GCs are read-only, searchable, and excluded from default lists and wake targets. Unarchive is owner-only (Q9). Archiving a parent does not archive children automatically; the UI offers it. No hard-delete path exists in the app.

### Software reactions
Reactors are consumers of the log, not authors of truth. `consumer_cursor(consumer, gc_id, last_seq)`; each consumer (notifier, Kanban projector, unread counter, search indexer) reads entries after its cursor, is idempotent on `(consumer, gc_id, seq)`, and can be rebuilt by replaying from zero. Triggers are **structured entries** (`task_update`, `pr_event`), never free-text parsing for authority. When software acts (a card moves), it appends a `system` entry saying so, so the explanation lives in the GC. `NOTIFY` is only a wake hint; consumers always read from the table.

### "What happened while I was away"
The client stores `read_state(principal, gc_id, last_read_seq)` and the server stores `last_seen_at`. Catch-up is a query over entries, grouped by GC: unread count (`head_seq - last_read_seq`), unread mentions, and entries of kind `structured` for PRs awaiting you, decisions and Council votes, blocked tasks and archive events. The dashboard calls `GetCatchUp(principal, since)` and gets counts plus the top N entry ids per category; opening a GC jumps to `last_read_seq + 1`. Summaries written by a model are a separate derived layer with entry-range citations, never a substitute for the log.

## Data and interfaces
```
gc(id uuid pk, parent_gc_id, type, title, tags text[], metadata jsonb,
   head_seq bigint, created_by, created_at, archived_at)
gc_member(gc_id, principal_id, role, joined_seq, left_seq)  -- pk (gc_id, principal_id)
entry(gc_id, seq bigint, id uuid, kind, author_kind, author_id, session_id,
      trust, tainted_at_write bool, reply_to_seq, body, target_seq, client_msg_id,
      created_at)                    -- pk (gc_id, seq); unique (gc_id, author_id, client_msg_id)
mention(gc_id, seq, target_principal_id)       entry_ref(gc_id, seq, ref_kind, ref_id, snapshot jsonb)
read_state(principal_id, gc_id, last_read_seq) consumer_cursor(consumer, gc_id, last_seq)
gc_access_request(id, gc_id, requester, status, decided_by, decided_seq)
```
Append: one transaction does `UPDATE gc SET head_seq = head_seq+1 ... RETURNING head_seq`, inserts the entry, mentions and refs in one `NpgsqlBatch`, then `pg_notify('gc', '{"gc":..,"seq":..}')`. The row lock serializes appends per GC, which gives gap-free seq at a cost that is fine at this scale.
```csharp
interface IGcLog {
  Task<AppendResult> AppendAsync(AppendRequest r, CancellationToken ct);   // idempotent by client_msg_id
  IAsyncEnumerable<Entry> ReadAsync(Guid gc, long afterSeq, int limit, Principal viewer, CancellationToken ct);
  Task<IReadOnlyList<EntryVersion>> GetHistoryAsync(Guid gc, long seq, Principal viewer, CancellationToken ct);
  Task<CatchUp> GetCatchUpAsync(Principal who, DateTimeOffset since, CancellationToken ct);
}
```
Every read takes the viewer, so DM and Thinking GC rules are enforced in one place.

## Behaviour and edge cases
- **Ordering:** seq order is the truth; `created_at` is informational. Clients de-duplicate on `(gc_id, seq)`.
- **Reconnect:** client sends `{gc_id: last_seq}` per subscribed GC; the server streams the gap from the table, then switches to live. A lost NOTIFY costs latency, not data.
- **Retries:** `client_msg_id` makes a repeated send return the original seq.
- **Flood:** per-agent and per-GC message budgets and reply-depth limits are enforced before append; a breach appends a visible `system` notice (loop guards; the evidence for exact limits is thin).
- **Partitioning:** monthly partitioning (Bazaar.Server.md) needs the partition key in the primary key; see Conflicts.
- **Poisoning:** an append-only log keeps injected text forever, so trust tags and quarantine entries are the only containment. Rendered remote images and auto-unfurls are off in agent-authored entries.
- **Erasure:** no deletion exists, so legal erasure cannot be done by the app; see Conflicts.
- **Access loss:** removing a member sets `left_seq`; they keep what they saw but not later entries.

## Testing
Real Postgres fixture (Testcontainers) for all of it. Cases: 200 concurrent appends to one GC yield gap-free seq; a replay from any N equals the live stream; a rolled-back append emits no NOTIFY; a duplicate `client_msg_id` returns the same seq; UPDATE and DELETE are denied to the app role; edit history round-trips and trust tag survives an edit; DM read by a non-member fails, passes after an approved request; mention spans for non-members are rejected; embed of a Vault doc the viewer cannot read returns a redacted snapshot; a consumer rebuilt from zero reaches identical state; archive blocks appends. A fake clock and fake principals drive catch-up tests. Avalonia.Headless covers the "edited" marker and history dialog (client design doc).

## Work packages
1. `Bazaar.Contracts` entry, principal, trust and envelope records (no wire decision). 
2. SQL migration for `gc`, `gc_member`, `entry`, mentions, refs; DB role grants. Depends on 1.
3. `IGcLog.AppendAsync` with seq counter, batch insert, NOTIFY, idempotency. Depends on 2.
4. `ReadAsync` and replay plus the LISTEN/fan-out hub. Depends on 3.
5. Edit entries and `GetHistoryAsync`. Depends on 3.
6. Membership, roles, access-request flow and the viewer-permission filter. Depends on 2.
7. Mention validation and inbox rows. Depends on 3, 6.
8. Trust tagging hook (takes the session taint flag from the taint design). Depends on 3.
9. `entry_ref` and embed snapshot resolver (PR, task, Vault interfaces stubbed). Depends on 3.
10. Consumer cursor framework plus a sample unread-counter consumer. Depends on 4.
11. `GetCatchUpAsync` and `read_state`. Depends on 7, 10.
12. Archive/unarchive and GC type defaults. Depends on 6.
13. Test fixture and replay/property tests. Parallel with 2.

## Conflicts
- **Partitioning vs primary key:** Bazaar.Server.md plans monthly partitioning, but a `(gc_id, seq)` primary key cannot partition by month unless `created_at` is in the key. Options: hash-partition by `gc_id`, or add `created_at` and lose the simple key. Needs a decision before the first migration.
- **Append-only vs privacy erasure:** the append-only log, edit history and "never purged" conflict with erasure duties when several humans join (Open Question 17). Per-person encryption with key destruction was suggested by research; needs counsel and would affect the `body` column design.
- **Owner reads all DMs vs multi-human privacy settings:** the owner's blanket read right may need an exception once another human member exists.
- **No-orchestrator evidence gap:** the coordination evidence note finds no published test of GC-only teams, and Cognition argues for shared full context while agents here may read only mention slices. The catch-up and reading rules (what an agent loads) belong to the agent runtime design; this doc only guarantees the full record exists.
- **"Thinking GC"** appears in the assignment but in no decided doc; treated here as PROPOSED.

## Questions for the owner
1. Should `seq` also number edits and system entries (this design: yes, one sequence, so replay is complete)? Default yes.
2. Partition the entry table by `gc_id` hash, by month with `created_at` in the key, or not at all for v1? Default: not at all until size demands it, with the key left partition-ready.
3. Who may invite into a GC: any member, or only the creator and owner? Default: any member, logged.
4. Who approves a DM access request: the owner only, or either DM member too? Default: the other DM member and the owner both must be notified, the owner can approve alone.
5. Should Thinking GCs exist in v1, and who can read them by default (agent plus owner)? Default: yes, owner-readable, others by request, opt-in per agent.
6. Can the owner edit another member's message, or only post a correction? Default: author only; corrections are new entries.
7. Edit window: unlimited, or locked after software or another agent acted on it? Default unlimited, with the "source edited after use" note.
8. Trust labels: are the five proposed values enough, and should humans other than the owner be `human_verified`? Default yes to both.
9. Unarchive: owner only, or any member of the GC? Default owner only.
10. `@all` and `@here`: allowed for agents, or owner only (flood risk)? Default owner and rate-limited agents.
11. Should the GC list sort by recent activity, last mention, or creator order on the dashboard, and what is the default "away" window? Default: unread mentions first, then activity.
12. Should embedded Vault docs pin to a commit hash always, or follow head? Default pin, with a "newer version" hint.
13. Max message size and attachment size? Default 32 KB text, 25 MB blobs, larger as a file entry.
14. Structured entries: may agents post them freely, or only through SDK calls that the server validates (Kanban moves require permission)? Default validated SDK calls only.
