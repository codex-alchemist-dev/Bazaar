# Kanban and Dependencies

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: `01-gc-data-model.md` (GC rows, pinned messages), `02-wire-protocol-and-sync.md` (board stream and resync), `04-postgres-schema-and-migrations.md` (conventions, NOTIFY), `06-agent-runtime.md` (agent ids, wake reasons), `09-work-life-simulation.md` (work-life scheduler), `10-council-and-voting.md` (gates), `15-security-taint-and-approvals.md` (tool dispatch, approvals, taint), `18-vault.md` (VaultWriter, Vault links), `21-github-integration.md` (PR events, merge). Used by: `23-observatory-and-usage.md` (events), `24-company-systems-core.md` (Interrupt System and Calendar consume events), `17-coding-agent-harness.md` (work-start guard), `11-reputation-and-peer-review.md` (outcomes), `27-client-architecture.md`, `28-windows-client.md`, `29-android-client.md` (board UI).

## Purpose
Kanban shows what can start, what waits on what, and who is accountable. It is a dependency manager, not a to-do list: the server refuses to start a task while a prerequisite is unfinished, so an agent cannot begin C before B is Done. Every state change has its reason in the task's GC.

## Decided inputs
- Dependency board with five columns: Backlog, Ready, In progress, Review, Done. "Blocked" is shown automatically on any card with an unfinished prerequisite; it is not a column (`CLAUDE.md`; `Bazaar.md` §4).
- GCs are the coordination system; events serve GCs, never replace them. GC history is append-only; messages may be edited, and every version is kept (`Bazaar.md` §2; DECISIONS-LOG round 1, C09).
- **Done on a PR card means the PR is merged; reviewer approval is a visible flag.** The owner merges from the Bazaar clients and the server performs the merge on signed approval (DECISIONS-LOG round 8, C28 and C25; round 12, C25).
- **Taint:** external content taints a session hard; hard taint gates only outward high-impact steps (posting, sending, spending, push or merge). A board move is not one (DECISIONS-LOG round 2, C06; round 6, C18).
- Interrupt System contract is owned by `24-company-systems-core.md` (round 9, C35). Company Calendar's `calendar_event` is owned there too (round 12, C45).
- Pub/sub: Postgres NOTIFY for fan-out; this board needs no Valkey or Garnet (round 1, C07).
- This doc owns the four task tables; agent runtime tables belong to `06` (round 6, C16; round 1, C04).
- Council: independent votes first, at most one follow-up round. A unanimous vote passes; only bigger changes go to the owner when a vote is split or tied (`Bazaar.md` §5; round 9, C32).
- Raw Npgsql, SQL-first repositories (`implementation-plan.md`).
- Not yet in `Bazaar.md`: agents announce state changes in the GC rather than changing state silently; tasks are pinned from GCs (see Conflicts 3).

## Design
- **Task service** [PROPOSED]: the only writer of task state, edges and gates.
- **Transition function** [PROPOSED]: pure `(state, action, facts) → result`, no I/O, so the rules test in isolation.
- **Derived Blocked** [PROPOSED]: the five columns are stored; Blocked is derived (any blocker not Done), so it cannot go stale.
- **Graph** [PROPOSED]: one edge per `B blocks C`. Self-edges and cycles are rejected on insert. Edges are changed by the blocked task's owner or the project owner.

| Transition | Actor | Guard | Side effects |
|---|---|---|---|
| Backlog → Ready | System (auto, when `kanban.auto_ready` is on) or owner | All blockers Done | GC line |
| Ready → In progress | Assignee | No unfinished blocker | Owner set; GC line |
| In progress → Review | Assignee | At least `kanban.review_min_links` PR or commit links (default 1) | Reviewer requested; GC line |
| Review → In progress | Reviewer | Reason required | GC line with reason |
| Review → Done | Reviewer (ungated, no PR, when `kanban.reviewer_ungated_done` is on); Council or owner if gated; PR-merged event for a PR card | Gate satisfied; PR merged where a PR is linked | Dependents recalculated; GC line |
| Done → Review | Owner | Reason required | Reopen; dependents show Blocked; GC line |
| Ready, In progress or Review → Backlog | Owner or assignee | Reason required | GC line |

- **Reopen** [PROPOSED]: with `kanban.reopen_downstream` = `hold` (default), downstream In progress tasks do not move. They show Blocked, and the assignee must post a decision in the GC: pause, or continue with a stated override. With `return_ready`, they move to Ready with a GC line. Reopening does not undo a merge; further change needs a new PR.
- **Auto-transitions** [PROPOSED]: with `kanban.auto_ready` on (default), Backlog → Ready runs in the same transaction as the last blocker's Done. Every automatic move writes a GC system line.
- **PR cards** [PROPOSED]: the merge is the Done signal. Doc 21 emits a merged `pr_event`; the task service moves the linked card to Done in the same transaction and writes a GC line. Reviewer approval sets `review_approved_by` and `review_approved_at` and moves nothing. A PR closed unmerged leaves its card in Review, flagged, until the assignee or owner moves it with a reason (`kanban.pr_closed_unmerged` = `hold_flagged`, default; `return_backlog` moves it to Backlog with a GC reason instead).

## Interception at tool dispatch [PROPOSED]
Agents change state only through a `kanban.transition` tool call or an authorised UI command. Both call the same `TransitionAsync`. Agents cannot write task tables.

1. The tool dispatcher (doc 15) calls the task service before any side effect.
2. The service locks the task and its blocker rows (`SELECT … FOR UPDATE`), checks the guard, and updates with `WHERE version = @expected`.
3. If a blocker is unfinished, the call returns a structured refusal (blocker list, and a suggestion to post in the blocker's GC). Nothing changes; `task.guard_refused` is emitted.

The guard controls the board, not the work; a coding harness (doc 17) should call the same guard before creating a worktree [PROPOSED]. Session taint does not refuse a board move (C06).

## Data and interfaces
These four tables are owned here (C16). Agent ids reference doc 06's `agent`; `origin_message_id` references a GC message row (doc 01).

```sql
tasks(id, project_id, title, state text CHECK (state IN ('backlog','ready','in_progress','review','done')),
      owner_agent_id NULL, gate text CHECK (gate IN ('none','council','owner')),
      origin_gc_id, origin_message_id NULL, due_at NULL,
      review_approved_by NULL, review_approved_at NULL, version bigint)
task_edges(blocker_id, blocked_id, created_by, created_at,
           PRIMARY KEY (blocker_id, blocked_id), CHECK (blocker_id <> blocked_id))
task_links(task_id, kind text CHECK (kind IN ('pr','commit','gc','vault_doc')), ref, PRIMARY KEY (task_id, kind, ref))
task_events(seq bigserial, task_id, kind, actor, payload jsonb, at)  -- append-only
```

```csharp
interface ITaskService {
    Task<TransitionResult> TransitionAsync(TaskId id, TaskState to, Actor actor,
        string? reason, long expectedVersion, CancellationToken ct);
    Task<EdgeResult> AddBlockerAsync(TaskId blocked, TaskId blocker, Actor actor, CancellationToken ct);
}
// TransitionResult: Ok(newVersion) | Refused(blockers) | Conflict(current) | Invalid(reason)
```

- **Cycle check** [PROPOSED]: recursive CTE under a per-project advisory lock, so concurrent inserts cannot both pass.
- **Links** [PROPOSED]: PR and commit checked against the provider (doc 21); Vault docs resolved through the Vault index (doc 18); GCs checked for existence. Links are informational, except that In progress → Review needs one.
- **Pinning** [PROPOSED]: any GC member can pin a message by default (`kanban.pin_permission`), creating a task with `origin_message_id`. The message is not edited.
- **Gates** [PROPOSED]: `council` needs a Council decision record (doc 10; written through VaultWriter, C24). `owner` needs owner approval through doc 15's approval flow (Tier 2). Hard taint does not gate Done. The PR merge is the outward step, and it runs on the owner's signed approval (docs 21 and 28).
- **Scheduling** [PROPOSED]: `due_at` is soft by default (`kanban.due_mode` = `soft`). Passing it emits `task.due_passed` and changes no state. With `hard`, a task past `due_at` cannot move Ready → In progress (structured refusal with reason). Agent timing comes from doc 09's work-life schedule. KPI and QPI: OPEN, no fields in v1.
- **Events** [PROPOSED]: `task.created`, `task.transitioned {from, to, actor, reason}`, `task.blocked` / `task.unblocked`, `task.edge_changed`, `task.guard_refused`, `task.gate_decided`, `task.due_passed`. Written to `task_events` in the same transaction and announced with Postgres NOTIFY, which PostgreSQL delivers only on commit. Consumers: Observatory (23), dashboard, Interrupt System and Calendar (24). Events never write GC messages; the task service does. Assignment and unblock wake reasons are owned by doc 06 (C08).

### Settings
Keys are proposed for doc 03's registry [PROPOSED]. Every change is written to the audit log (doc 15). Settings never loosen a DECIDED rule: no settings for the PR-merge Done rule (C28), hard-taint exemption of board moves (C06), or the only-the-task-service-writes rule.

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `kanban.auto_ready` | bool | true | project | project owner | No |
| `kanban.reopen_downstream` | enum: `hold`, `return_ready` | `hold` | project | project owner | No |
| `kanban.blocked_start_roles` | enum: `owner`, `owner_or_assignee` | `owner` | project | install owner | Yes (owner) |
| `kanban.review_min_links` | int, 0 to 5 | 1 | project | project owner | No |
| `kanban.reviewer_ungated_done` | bool | true | project | project owner | No |
| `kanban.abandoned_satisfies_blockers` | bool | false | project | project owner | Yes (owner) |
| `kanban.due_mode` | enum: `soft`, `hard` | `soft` | project | project owner | No |
| `kanban.pin_permission` | enum: `any_member`, `owner` | `any_member` | project | project owner | No |
| `kanban.pr_closed_unmerged` | enum: `hold_flagged`, `return_backlog` | `hold_flagged` | project | project owner | No |
| `kanban.board_scope` | enum: `project`, `global` | `project` | install | install owner | No |
| `kanban.max_blockers_per_task` | int | 50 | install | install owner | No |
| `kanban.max_tasks_per_project` | int | 10000 | install | install owner | No |

## Behaviour and edge cases
- **Concurrency**: a stale `version` returns Conflict with current state. Edge writes lock endpoints in ascending id order. If two agents claim one task, one wins; the other gets a refusal naming the new owner.
- **Reconnect**: board clients resync from a snapshot with a project revision on the `board` stream (registered in doc 02, which owns the replay rules, C15). `task_events.seq` orders events inside the server and is not a client cursor unless doc 02 adopts it.
- **Cancellation gap**: the five columns stay, so there is no Cancelled state. By default (`kanban.abandoned_satisfies_blockers` = false) dependents of an abandoned task stay Blocked until the owner re-points or removes the edge.
- **Deletion**: not in v1; use Ready/In progress/Review → Backlog with a reason.
- **Limits** [PROPOSED]: `kanban.max_blockers_per_task` (default 50) and `kanban.max_tasks_per_project` (default 10,000), to bound the cycle check. This doc owns these limits (C36; settings per DECISIONS-LOG round 13).
- **Security**: only the task service writes state, so injected text cannot change the board directly. Owner overrides are logged with the actor.

## Testing
No models or displays needed. Real Postgres via an xUnit v3 fixture; `Avalonia.Headless` for the board.
- Transition table: a theory over every from/to pair (allowed, or refused with the right reason).
- Cycles: self-edge, three-node cycle, two concurrent inserts that would close one.
- Auto Ready: last blocker Done moves the dependent in the same transaction, with a GC line.
- Claim race: exactly one of two agents wins. Stale version returns Conflict and changes nothing.
- Reopen: state unchanged, Blocked derived, GC line required.
- PR card: reviewer approval sets the flag and leaves the card in Review; the merged event moves it to Done; a closed-unmerged PR does not.
- Guard refusal: no state change; `task.guard_refused` emitted once.
- Notify: events are announced once, after commit, never after rollback.
- Headless: five columns render; Blocked badge and blocker chips show; a refused drag shows its reason.

## Work packages
1. Schema and migration for the four tables. Depends on Wave 0.
2. Transition table as pure code with tests. None.
3. Graph service: edges, cycle check, derived blocked query. Depends on 1.
4. Task service: locked transitions, version checks, auto Ready, reopen. Depends on 2, 3.
5. Dispatch guard and `kanban.transition` refusal contract. Depends on 4.
6. GC bridge: reason lines and pin-from-message. Depends on 4.
7. Link validation (PR, commit, GC, Vault). Depends on 1.
8. Event outbox and Postgres NOTIFY fan-out. Depends on 1 and doc 04's NOTIFY layer.
9. Gate records and Council hook. Depends on 4 and doc 10 (VaultWriter, doc 18).
10. Board view model and Avalonia board. Depends on 8.
11. Deadline scanner for `due_at`. Depends on 8 and doc 09's scheduler.
12. PR-merged Done hook and reviewer-approval flag, consuming doc 21's `pr_event`. Depends on 4 and doc 21.

## Conflicts
1. **Automatic moves vs talk-first.** Auto Ready and reopen change state without an agent acting. A GC line on each move mitigates it; the owner should confirm that reading.
2. **Board vs work.** The guard stops the board lying, not agents writing code early. Only the harness guard (doc 17) mitigates this.
3. **Unrecorded decisions.** Talk-first and pinning are not in `Bazaar.md`; the owner should add them so the doc set agrees.
4. **Interrupt contract (doc 24).** Doc 24 must list which task events it consumes and which of them may raise an interrupt. This doc only emits events.
5. **Reputation (doc 11).** If Review → Done feeds reputation, blocker-caused waits must not count as failures.
6. **Unverified pattern.** The `task_edges` and row-lock design is an unsourced inference (feasibility note, Key Question 6).
7. **Doc 01 must change.** It still describes triggers as `task_update` / `pr_event` entries consumed by a "Kanban projector". Per C28 the task service is the only writer and writes the GC system lines.
8. **Doc 15 must confirm** that its Tier 2 list does not include board moves and names the PR merge as the outward step (C06).
9. **Doc 21 must emit** a merged `pr_event` to the task service; doc 21 and doc 04 must drop any task-table outline of their own (C16).

## Questions for the owner
1. **Assignees**: one accountable owner per task? Default: yes, with helpers mentioned in the GC. (Shapes the `owner_agent_id` column, so it is one answer, not a setting.)
2. **KPI and QPI**: what do they measure, and are they v1? Default: not in v1; no fields until defined.
3. **Task GCs**: does each task get its own GC? Default: no; it uses its origin GC, and a child GC is created on request. (Changes the GC creation model, so it is one answer, not a setting.)

MOVED TO SETTINGS: kanban.auto_ready, kanban.reopen_downstream, kanban.blocked_start_roles, kanban.review_min_links, kanban.reviewer_ungated_done, kanban.abandoned_satisfies_blockers, kanban.due_mode, kanban.pin_permission, kanban.pr_closed_unmerged, kanban.board_scope, kanban.max_blockers_per_task, kanban.max_tasks_per_project (DECISIONS-LOG round 13)
