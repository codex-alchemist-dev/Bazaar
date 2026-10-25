# Kanban and Dependencies

Status: DRAFT for owner review (2026-10-25) · Owner questions: 12
Depends on: `Bazaar.md`, `Bazaar.Server.md`, `Bazaar.Soul.md`, `Bazaar.Windows.md`. Used by: Observatory and dashboard (events), Company Calendar, Council (gates), Interrupt System (events), coding harness (work-start guard). No sibling design docs exist yet; contracts are stated here as [PROPOSED].

## Purpose
Kanban shows what can start, what waits on what, and who is accountable. It is a dependency manager, not a to-do list: the server refuses to start a task while a prerequisite is unfinished, so an agent cannot begin C before B is Done. Every state change has its reason in the task's GC.

## Decided inputs
- Dependency board; columns Backlog, Ready, In progress, Review, Done; "Blocked" shown automatically on any card with an unfinished prerequisite (`CLAUDE.md`; `Bazaar.md` §4).
- GCs are the coordination system; events serve GCs, never replace them. GC history is append-only (`Bazaar.md` §2).
- Council: independent votes first, at most one follow-up round, ties to the owner (`Bazaar.md` §5).
- Interrupt System and Company Calendar in v1 scope; designs not written (`Bazaar.md` §4).
- Postgres with raw Npgsql and SQL-first repositories (`implementation-plan.md`).
- From the assignment brief, not yet in `Bazaar.md`: agents announce state changes in the GC rather than changing state silently; tasks are pinned from GCs.

## Design
- **Task service** [PROPOSED]: the only writer of task state, edges and gates.
- **Transition function** [PROPOSED]: pure `(state, action, facts) → result`, no I/O, so the rules test in isolation.
- **Derived Blocked** [PROPOSED]: the five columns are stored; Blocked is derived (any blocker not Done), so it cannot go stale.
- **Graph** [PROPOSED]: one edge per `B blocks C`. Self-edges and cycles are rejected on insert. Edges are changed by the blocked task's owner or the project owner.

| Transition | Actor | Guard | Side effects |
|---|---|---|---|
| Backlog → Ready | System (auto) or owner | All blockers Done | GC line |
| Ready → In progress | Assignee | No unfinished blocker | Owner set; GC line |
| In progress → Review | Assignee | At least one PR or commit link | Reviewer requested; GC line |
| Review → In progress | Reviewer | Reason required | GC line with reason |
| Review → Done | Reviewer, or Council or owner if gated | Gate satisfied | Dependents recalculated; GC line |
| Done → Review | Owner | Reason required | Reopen; dependents show Blocked; GC line |
| Open → Backlog | Owner or assignee | Reason required | GC line |

- **Reopen** [PROPOSED]: downstream In progress tasks do not move. They show Blocked, and the assignee must post a decision in the GC: pause, or continue with a stated override.
- **Auto-transitions** [PROPOSED]: Backlog → Ready runs in the same transaction as the last blocker's Done. Every automatic move writes a GC system line.

## Interception at tool dispatch [PROPOSED]
Agents change state only through a `kanban.transition` tool call or an authorised UI command. Both call the same `TransitionAsync`. Agents cannot write task tables.

1. The dispatcher calls the task service before any side effect.
2. The service locks the task and its blocker rows (`SELECT … FOR UPDATE`), checks the guard, and updates with `WHERE version = @expected`.
3. If a blocker is unfinished, the call returns a structured refusal (blocker list, and a suggestion to post in the blocker's GC). Nothing changes; `task.guard_refused` is emitted.

The guard controls the board, not the work. A coding harness should call the same guard before creating a worktree for a task [PROPOSED].

## Data and interfaces
```sql
tasks(id, project_id, title, state text CHECK (state IN ('backlog','ready','in_progress','review','done')),
      owner_agent_id NULL, gate text CHECK (gate IN ('none','council','owner')),
      origin_gc_id, origin_message_id NULL, due_at NULL, version bigint)
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
- **Links** [PROPOSED]: PR and commit checked against the provider; Vault docs resolved through the Vault index; GCs checked for existence. Links are informational, except that In progress → Review needs one.
- **Pinning** [PROPOSED]: any GC member can pin a message, creating a task with `origin_message_id`. The message is not edited.
- **Gates** [PROPOSED]: `council` needs a Council decision record; `owner` needs owner approval. A session that has seen untrusted input needs owner approval for Done (`Bazaar.Server.md` §13).
- **Scheduling** [PROPOSED]: `due_at` is soft. Passing it emits `task.due_passed` and changes no state. Agent timing comes from the work-life schedule. KPI and QPI: OPEN, no fields in v1.
- **Events** [PROPOSED]: `task.created`, `task.transitioned {from, to, actor, reason}`, `task.blocked` / `task.unblocked`, `task.edge_changed`, `task.guard_refused`, `task.gate_decided`, `task.due_passed`. Written to `task_events` in the same transaction, published after commit. Consumers: Observatory, dashboard, Interrupt System, Calendar. Events never write GC messages; the transition service does.

## Behaviour and edge cases
- **Concurrency**: a stale `version` returns Conflict with current state. Edge writes lock endpoints in ascending id order. If two agents claim one task, one wins; the other gets a refusal naming the new owner.
- **Reconnect**: clients keep the last seen `task_events.seq` and replay from it.
- **Cancellation gap**: no Cancelled state, so dependents of an abandoned task stay Blocked (Question 7).
- **Deletion**: not in v1; use Open → Backlog with a reason.
- **Limits** [PROPOSED]: 50 blockers per task, 10,000 tasks per project, to bound the cycle check.
- **Security**: only the task service writes state, so injected text cannot change the board directly. Owner overrides are logged with the actor.

## Testing
No models or displays needed. Real Postgres via an xUnit v3 fixture; `Avalonia.Headless` for the board.
- Transition table: a theory over every from/to pair (allowed, or refused with the right reason).
- Cycles: self-edge, three-node cycle, two concurrent inserts that would close one.
- Auto Ready: last blocker Done moves the dependent in the same transaction, with a GC line.
- Claim race: exactly one of two agents wins. Stale version returns Conflict and changes nothing.
- Reopen: state unchanged, Blocked derived, GC line required.
- Guard refusal: no state change; `task.guard_refused` emitted once.
- Outbox: events publish once after commit, never after rollback.
- Headless: five columns render; Blocked badge shows blocker chips; a refused drag shows its reason.

## Work packages
1. Schema and migration for the four tables. Depends on Wave 0.
2. Transition table as pure code with tests. None.
3. Graph service: edges, cycle check, derived blocked query. Depends on 1.
4. Task service: locked transitions, version checks, auto Ready, reopen. Depends on 2, 3.
5. Dispatch guard and `kanban.transition` refusal contract. Depends on 4.
6. GC bridge: reason lines and pin-from-message. Depends on 4.
7. Link validation (PR, commit, GC, Vault). Depends on 1.
8. Event outbox and pub-sub fan-out. Depends on 1 and the pub-sub provider.
9. Gate records and Council hook. Depends on 4 and the Council design.
10. Board view model and Avalonia board. Depends on 8.
11. Deadline scanner for `due_at`. Depends on 8 and the work-life scheduler.

## Conflicts
1. **Automatic moves vs talk-first.** Auto Ready and reopen change state without an agent acting. A GC line on each move mitigates it; the owner should confirm that reading.
2. **Board vs work.** The guard stops the board lying, not agents writing code early. Only the harness guard mitigates this.
3. **Unrecorded decisions.** Talk-first and pinning are not in `Bazaar.md`; add them so the doc set agrees.
4. **Interrupt System** is v1 scope with no design; the event-consumer contract here is a guess.
5. **Reputation.** If Review → Done feeds reputation, blocker-caused waits must not count as failures. The coordination evidence note asks for scores anchored to hard outcomes.
6. **Unverified pattern.** The `task_edges` and row-lock design is an unsourced inference (feasibility note, Key Question 6).

## Questions for the owner
1. **Auto Ready**: should Backlog → Ready be automatic when the last blocker is Done? Default: yes, with a GC line.
2. **Reopen**: should downstream In progress tasks return to Ready? Default: no; the assignee decides, so no work is silently rolled back.
3. **Override**: who may start a Blocked task? Default: owner only, with a logged reason.
4. **Review evidence**: is one PR or commit link enough? Default: yes.
5. **Review authority**: may a reviewer agent move Review → Done on ungated tasks? Default: yes; gated tasks need Council or owner.
6. **Assignees**: one accountable owner per task? Default: yes, with helpers mentioned in the GC.
7. **Cancellation**: add a Cancelled state that counts as Done for dependents? Default: yes, with a GC line.
8. **Deadlines**: soft (notice only) or hard (block start)? Default: soft.
9. **KPI and QPI**: what do they measure, and are they v1? Default: not in v1; no fields until defined.
10. **Pinning**: may any GC member pin, or only the owner? Default: any member.
11. **Task GCs**: does each task get its own GC? Default: no; it uses its origin GC, and a child GC is created on request.
12. **Board scope**: one board per project, or one global board? Default: per project.
