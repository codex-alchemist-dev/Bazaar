# Company Systems (Core)

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 07-soul-system.md, 09-work-life-simulation.md, 10-council-and-voting.md, 12-kanban-and-dependencies.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 18-vault.md · Used by: 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 07-soul-system.md, 09-work-life-simulation.md, 10-council-and-voting.md, 12-kanban-and-dependencies.md, 23-observatory-and-usage.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md

## Purpose
Four v1 company systems: a Constitution every Soul reads before work, an org graph of reporting and loans, a per-agent interrupt stack, and one calendar of plans, releases and vacations. Each is a record or a rule that other subsystems read. When one changes, its owning service writes a GC line. Events never write GC messages, and none of these replaces a GC.

## Decided inputs
- No master orchestrator; GCs are the coordination system; events serve GCs (Bazaar.md §1–2; CLAUDE.md).
- Constitution, Org Chart, Interrupt System and Company Calendar are v1 scope (CLAUDE.md; Bazaar.md §4). The Constitution is not drafted, and no design existed for the other three.
- Table ownership: this doc owns `constitution_ack`, `org_relation`, `interrupt_frame` and `calendar_event` (DECISIONS-LOG round 1 C04, round 6 C16). Agent, turn, lease and `inbox_item` stay in 06; Soul versions in 07; schedule and availability in 09.
- [DECIDED, DECISIONS-LOG round 9, C35, 2026-10-25] Per-agent interrupt stack, depth 2; a third push is refused. An owner interrupt bypasses vacation only when tagged `emergency`. Replaces 06's flat `Interrupt` (06 now calls `PushAsync`/`PopAsync`).
- [DECIDED, DECISIONS-LOG round 12, C45, 2026-10-25] This doc owns `calendar_event`; 09 consumes it.
- [DECIDED, DECISIONS-LOG round 7, C24, 2026-10-25] Decision records are `decisions/NNNN-slug.md` in the Vault, written only through the single `VaultWriter`.
- [DECIDED, DECISIONS-LOG round 11, C38, 2026-10-25] The Vault holds durable written artifacts, including the Constitution.
- [DECIDED, DECISIONS-LOG round 2, C06, 2026-10-25] External content taints a session hard; agent text taints softly; Tier 2 actions are gated on hard taint (15).
- [DECIDED, DECISIONS-LOG round 1, C07, 2026-10-25] Postgres NOTIFY carries GC fan-out. This doc has no cache or pub/sub dependency.
- Vacation beats curfew, curfew beats shift; availability never grants tool permissions (09).
- Constitution changes are Council motions by default (`council.bigger_change_classes`, doc 10) [PROPOSED]. This replaces the earlier owner-only draft (18 Q2, PROPOSED).
- Only the task service writes Kanban state (12, PROPOSED).

## Design

### 1. Company Constitution
- **Storage [PROPOSED].** `company/Constitution.md` in the private company Vault (18). One heading per section: Mission, Vision, Culture, Engineering principles, Best practices, Decision index. The index links `decisions/NNNN-*.md` records (DECIDED above).
- **Changes [PROPOSED].** Agents propose a branch plus a decision record in their GC. Each change is a `bigger_change` motion by default. A unanimous Council vote passes it; a split or tied vote goes to the owner (C32). VaultWriter merges only after that approval. Agents never write `company/` directly.
- **Onboarding [PROPOSED].** A Soul reads the accepted commit, records `constitution_commit` on its Soul version (07), and posts an acknowledgement in its GC. No accepted commit, no activation. Re-acknowledgement follows `constitution.reacknowledge_policy`.
- **Propagation [PROPOSED].** An accepted commit emits `constitution.changed {from, to, sections[]}`. The runtime (a) adds a low-priority `constitution` kind to 06's `inbox_item` for each active Soul, digest-class, which does not wake it; (b) posts one notice in the General Bazaar GC (a well-known type registered in 01); (c) refreshes the excerpt in the system prefix from the next turn, within `constitution.excerpt_token_budget`. Running turns keep their old commit.

### 2. Organizational Chart
- **Model [PROPOSED].** Edges between agents, scoped to a project or company-wide. v1 kinds: `reports_to` (approval routing and escalation only) and `lent_to` (a project borrows an agent). Cover pairs stay in 09; this view reads them.
- **Routing [PROPOSED].** An agent may have one `reports_to` per project scope and any number of loans. Project items go to that project's `reports_to` (`org.project_item_route`); company items go to the owner.
- **Loans [PROPOSED].** A loan adds the agent to the project's member list with an end date. It does not change the agent's home schedule, availability or Soul. Who approves loans and `reports_to` edits is `org.relation_approver`.
- **No authority [PROPOSED].** Org edges grant nothing. Access comes from grants and the Identity Service (14, 15).
- **Kanban [PROPOSED].** A task owner must be a project member at assignment. When a loan ends, open cards stay where they are; the service posts a GC line and the project lead reassigns or extends.

### 3. Interrupt System
No source design existed before this doc; the stack contract is DECIDED (round 9), the rest is PROPOSED.
- **Model [DECIDED, C35].** One stack of frames per agent, depth 2. Push interrupts; pop resumes. Only the top frame runs.
- **Sources [PROPOSED].** Owner (any agent) and system (build broken, security event). Agents never interrupt agents; they mention them (Q3).
- **Push [PROPOSED].** Write the frame and a GC line with reason and source. Set a cancel flag. The runtime checks it at the next tool boundary (06), persists the current turn as `interrupted` (06 owns turns), releases the lease and starts a turn for the new top frame. An in-flight model call finishes first.
- **Pop [PROPOSED].** Resume note posted to the GC. The suspended frame becomes top and resumes from its last persisted step. Completed tool results are reused (06).
- **Schedule.** An owner interrupt bypasses vacation only when tagged `emergency` [DECIDED, C35]. Bypass of curfew and break is set by doc 09's `schedule.emergency_bypasses_curfew` and `schedule.emergency_bypasses_break`, both on by default [PROPOSED]. No setting clears a provider state or cap. System interrupts follow the schedule, except security events [PROPOSED].
- **Kanban [PROPOSED].** An interrupt never changes card state. The suspended card keeps its claim for `interrupt.suspended_claim_hours` working hours, then the service posts a GC prompt: pause or release.
- **Recovery [PROPOSED].** The stack is rebuilt from the frame table after a crash, never from memory.
- **Presence [PROPOSED].** The stack indicator sits on the presence chip. Labels and overlays follow the DECISIONS-LOG round 11 (C43) list; adding a label needs the owner.

### 4. Company Calendar
- **Ownership [DECIDED, C45].** This doc owns `calendar_event`. The calendar writes no availability. The 09 availability machine is the single writer and reads events with `affects_availability`.
- **Kinds [PROPOSED].** vacation (flagged `affects_availability`); planning, release, freeze, milestone (informational). Which kinds change availability is doc 09's `schedule.calendar_availability_kinds`; v1 default is vacation only.
- **Requests [PROPOSED].** An agent requests vacation in its GC. Confirmation follows `calendar.vacation_confirmer`. Release and milestone events are created as set by `calendar.release_creators`. Changes are new versions; cancel is a status change, never a delete.
- **Links [PROPOSED].** Optional `source_gc_id`, Vault doc and task references (12), all informational.
- **Personal fields [PROPOSED].** `title` and any vacation note are personal fields under C01: stored as per-person encrypted payloads (04).

## Data and interfaces
```sql
-- Free-text title/reason columns are encrypted personal fields (C01, doc 04)
constitution_ack (soul_id, soul_version, constitution_commit char(40), acked_at)
org_relation (id, kind text CHECK (kind IN ('reports_to','lent_to')), from_agent_id, to_agent_id,
              project_id NULL,                      -- NULL = company-wide
              starts_at, ends_at NULL, created_by, version bigint)
  -- UNIQUE (from_agent_id, project_id) WHERE kind = 'reports_to' AND ends_at IS NULL
interrupt_frame (id, agent_id, parent_frame_id NULL,
                 source text CHECK (source IN ('owner','system_build','system_security')),
                 reason text, gc_id, state text CHECK (state IN ('active','suspended','done','cancelled')),
                 resume_turn_id NULL, pushed_at, ttl_at, version bigint)
  -- UNIQUE (agent_id) WHERE state = 'active'
calendar_event (id, kind text, title, starts_at timestamptz, ends_at timestamptz, all_day bool,
                tz text, agent_id NULL, project_id NULL, affects_availability bool,
                status text CHECK (status IN ('proposed','confirmed','cancelled')),
                source_gc_id, version bigint)
```
```csharp
// As built in INT1 (migration 0015, src/Bazaar.Server/Interrupts). resumeNote, the GC line and the emergency tag move to WP6.
interface IInterruptStackService {
    Task<InterruptFrame> PushAsync(Guid agentId, InterruptSource source, string reason, Guid gcId, DateTimeOffset? ttlAt, CancellationToken ct);
    Task<InterruptFrame?> PopAsync(Guid agentId, Guid frameId, CancellationToken ct); // idempotent
    Task<IReadOnlyList<InterruptFrame>> GetStackAsync(Guid agentId, CancellationToken ct);
}
interface IOrgGraph { Task<IReadOnlyList<OrgEdge>> RoutesAsync(ProjectId p, AgentId a, CancellationToken ct); }
interface ICalendar { Task<CalendarEvent> SetAsync(CalendarDraft d, long? expectedVersion, CancellationToken ct); }
```

### Settings
Owned by this doc; the registry is doc 03 [PROPOSED]. Changes are audited (doc 15 `audit_log`).

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `constitution.excerpt_token_budget` | int (tokens) | 1500 | install | operator | no; audit entry |
| `constitution.reacknowledge_policy` | enum: `every_accepted_change` (only v1 option) | `every_accepted_change`, at next shift start | install | operator | no; audit entry |
| `org.relation_approver` | enum: `owner`, `project_lead` | `owner` | project | owner | yes |
| `org.project_item_route` | enum: `project_reports_to`, `owner` | `project_reports_to` | project | owner | yes |
| `interrupt.suspended_claim_hours` | int (working hours, per agent schedule) | 4 | install | operator | no; audit entry |
| `calendar.vacation_confirmer` | enum: `owner`, `project_lead` | `owner` | project | owner | yes |
| `calendar.release_creators` | enum: `owner`, `owner_or_project_lead` | `owner_or_project_lead` | project | owner | no; audit entry |
| `calendar.display_timezone` | enum: `viewer`, `agent` | `viewer` | install | each viewer (client preference) | no |

Settings owned by other docs and used here: `council.bigger_change_classes` (10), `schedule.emergency_bypasses_curfew`, `schedule.emergency_bypasses_break`, `schedule.calendar_availability_kinds` (09).

## Behaviour and edge cases
- A Constitution commit that fails to parse, or lacks a required heading, is rejected. The previous commit stays active; nothing is half-applied.
- Org: `reports_to` cycles within one scope are rejected on insert, under the same advisory-lock pattern as 12. Ended loans are read-only.
- Interrupts: depth 2 (an owner push over one system frame). A third push is refused and the owner is told. Pops are idempotent by frame id. Identical system pushes within 60 s collapse into one frame.
- Calendar: overlapping vacations are merged by the availability machine, not the table. Inverted ranges are refused. Cancelling a live vacation restores the pre-vacation availability through 09 transitions.
- Security: only the owner's authenticated messages or system events create interrupts. Text from untrusted sources cannot push a frame (C06).

## Testing
Real Postgres (xUnit v3 fixture) for constraints, with the schema built by the 04 `migrate` command (C44). Pure tests for routing and stack rules. Fake clock (`TimeProvider`) for TTL and DST. Fake gateway for interrupt timing at tool boundaries.
- Constitution: an accepted commit changes the excerpt from the next turn only; a running turn keeps its commit; no activation without an accepted commit; re-acknowledgement records the commit; a split vote on a `bigger_change` escalates to the owner.
- Org: two `reports_to` in one scope refused; a cycle refused; a project item routes to the project lead, a company item to the owner; a loan ending leaves the card owner unchanged and posts a GC line.
- Interrupt: a push waits for the in-flight model call, then takes effect at the tool boundary; a pop resumes without repeating completed tools; an agent-sourced push is refused; an owner push tagged `emergency` during vacation wakes the agent, an untagged one does not; with `schedule.emergency_bypasses_curfew` off, a tagged push waits for the shift; a system push queues during curfew; a third nested push is refused; crash with two frames restores the top frame; card state unchanged.
- Calendar: vacation beats curfew; release events leave availability unchanged; DST day; cancel restores the pre-vacation availability; all-day events across zones.
- `Avalonia.Headless`: org lens switch, calendar month chips, stack indicator on the presence chip.

## Work packages
1. Constitution section parser and accepted-commit check. Needs 18 WP4, WP9.
2. `constitution.changed` event, inbox kind, excerpt refresh. Needs 1, 06 WP2, 07 WP2.
3. Org schema, unique and cycle constraints, SQL repositories. Needs the 04 core migrations.
4. Org routing function and loan lifecycle with GC lines. Needs 3, 12 WP6.
5. Interrupt frame table and stack service. Needs 04 migrations. **Done in INT1:** migration 0015 and `IInterruptStackService`, with a real-Postgres test suite. Not yet done: GC lines, the cancel flag, `resume_turn_id`, and the emergency tag (WP6).
6. Scheduler wiring at tool boundaries; resume. Needs 5, 06 WP3, WP5.
7. Calendar table, service, GC lines. Needs the 04 core migrations.
8. Availability consumes calendar vacations. Needs 7, 09 WP3, WP9.
9. Client views: org, calendar, stack indicator. Needs 27, 2, 4, 5, 7.

Order: 1→2; 3→4; 5→6; 7→8; all → 9.

## Conflicts
1. **Constitution is v1 but undrafted, and onboarding is blocked on it** (07, Bazaar.md §4). No Soul can activate in v1 until the owner approves a commit (Q1).
2. **Council versus owner approval for Constitution changes.** Doc 10 makes every Constitution change a `bigger_change` by default [PROPOSED], so this doc now follows that default. It flips the earlier owner-only draft (18 Q2). The owner should confirm the default, or set `council.bigger_change_classes` to exclude it. The eligible-voter definition for "perfect" (unanimous) is still FOLLOW-UP (C32).
3. **Authority vs Council.** `reports_to` must never outweigh a vote (10). It routes approvals only.
4. **Cache cost.** The Constitution excerpt sits in the cached prefix (06; cache-write columns in `gw_usage`, C33). Each change invalidates it once per agent.
5. **Privacy.** The Constitution is private company material and must not enter the public platform repo (CLAUDE.md; 18).

## Questions for the owner
1. **Who drafts Constitution v1?** Default: you write a one-page seed (mission, vision, culture) in your own words; Claude drafts the other sections from DECIDED items; you approve one commit before any Soul activates. Why: those sections are your voice.
2. **Org relation kinds.** Default: `reports_to` and `lent_to` only; covers stay in 09. Why: the kinds are a CHECK constraint, so adding one is a schema change and needs one answer.
3. **Who may interrupt?** Default: you, for any agent at any time; build-broken and security events are system sources; agents never interrupt agents. Why: the source list is in the schema and the agent rule is a security boundary (C06). Confirm the system sources and the agent rule.

MOVED TO SETTINGS: constitution.excerpt_token_budget, constitution.reacknowledge_policy, org.relation_approver, org.project_item_route, interrupt.suspended_claim_hours, calendar.vacation_confirmer, calendar.release_creators, calendar.display_timezone, council.bigger_change_classes (doc 10), schedule.emergency_bypasses_curfew, schedule.emergency_bypasses_break, schedule.calendar_availability_kinds (doc 09) (DECISIONS-LOG round 13)

Answered in DECISIONS-LOG:
- ANSWERED: stack depth 2; a third push is refused (DECISIONS-LOG round 9, C35).
- ANSWERED: an owner interrupt bypasses vacation only when tagged `emergency` (DECISIONS-LOG round 9, C35).
