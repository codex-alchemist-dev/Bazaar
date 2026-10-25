# Company Systems (Core)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 14
Depends on: 06-agent-runtime.md, 07-soul-system.md, 09-work-life-simulation.md, 12-kanban-and-dependencies.md, 15-security-taint-and-approvals.md, 18-vault.md · Used by: 06, 07, 09, 10-council-and-voting.md, 12, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md

## Purpose
Four v1 company systems: a Constitution every Soul reads before work, a relationship map that can differ by project, a formal interrupt and resume stack, and one calendar of plans, releases and vacations. Each one is a record or a rule that other subsystems read. Each writes a GC line when it changes; none replaces a GC.

## Decided inputs
- No master orchestrator; GCs are the coordination system; events serve GCs (Bazaar.md §1–2).
- Constitution (`/company`: culture, principles, mission, decision records) and every Soul onboarding with it are v1 scope; not drafted (Bazaar.md §4, Open Q1).
- Org Chart, Interrupt System and Company Calendar are v1 scope; no designs exist (Bazaar.md §4).
- Vacation beats curfew, curfew beats shift; availability never grants tool permissions (09).
- Constitution changes need owner approval (18 Q2, PROPOSED).
- Taint tracking with an owner gate on high-impact actions (Bazaar.Server.md §13, DECIDED).
- Only the task service writes Kanban state; events never write GC messages (12, PROPOSED).
- Owner interrupting any agent, at any time, comes from the assignment brief only. It is not in Bazaar.md. Treated as PROPOSED here.

## Design

### 1. Company Constitution
- **Storage [PROPOSED].** `company/Constitution.md` in the private company Vault (18). One heading per section: Mission, Vision, Culture, Engineering principles, Best practices, Decision index. The index links `decisions/NNNN-*.md`; decision records keep their own files.
- **Changes [PROPOSED].** Agents propose a branch plus a decision record in their GC. VaultWriter merges only after owner approval. Agents never write `company/` directly.
- **Onboarding [PROPOSED].** A Soul reads the accepted commit, records `constitution_commit` on its Soul version (07), and posts an acknowledgement in its GC. No accepted commit, no activation.
- **Propagation [PROPOSED].** An accepted commit emits `constitution.changed {from, to, sections[]}`. The runtime (a) adds a low-priority `constitution` inbox item per active Soul, which is digest-class and does not wake it; (b) posts one notice in the General Bazaar GC; (c) refreshes the excerpt in the system prefix from the next turn. Running turns keep their old commit.

### 2. Organizational Chart
- **Model [PROPOSED].** Edges between agents, scoped to a project or company-wide. v1 kinds: `reports_to` (approval routing and escalation only) and `lent_to` (a project borrows an agent). Cover pairs stay in 09; this view reads them.
- **Routing [PROPOSED].** An agent may have one `reports_to` per project scope and any number of loans. Project items go to that project's `reports_to`; company items go to the owner.
- **Loans [PROPOSED].** A loan adds the agent to the project's member list with an end date. It does not change the agent's home schedule, availability or Soul.
- **No authority [PROPOSED].** Org edges grant nothing. Access comes from grants and the Identity Service (14, 15).
- **Kanban [PROPOSED].** A task owner must be a project member at assignment. When a loan ends, open cards stay where they are; the service posts a GC line and the project lead reassigns or extends.

### 3. Interrupt System
No source design exists. Everything here is a proposal.
- **Model [PROPOSED].** One stack of frames per agent. Push interrupts; pop resumes. Only the top frame runs.
- **Sources [PROPOSED].** Owner (any agent); system (build broken, security event). Agents never interrupt agents; they mention them.
- **Push.** Write the frame and a GC line with reason and source. Set a cancel flag. The runtime checks it at the next tool boundary (06), persists the current turn as `interrupted` (03), releases the lease and starts a turn for the new top frame. An in-flight model call finishes first.
- **Pop.** Resume note posted to the GC. The suspended frame becomes top and resumes from its last persisted step. Completed tool results are reused (06).
- **Schedule.** An owner interrupt may bypass curfew, break and vacation, as an emergency override (09). It never clears a provider state. System interrupts follow the schedule, except security events.
- **Kanban.** An interrupt never changes card state. The suspended card keeps its claim until the frame's time-to-live (TTL), then the service posts a GC prompt: pause or release.
- **Recovery.** The stack is rebuilt from the frame table after a crash, never from memory.

### 4. Company Calendar
- **Ownership [PROPOSED].** This doc owns `calendar_event` (09 defines a draft). The calendar writes no availability. The 09 availability machine, the single writer, reads events with `affects_availability`.
- **Kinds.** vacation (affects availability); planning, release, freeze, milestone (informational). Only vacation changes availability in v1 (09 Q11).
- **Requests.** An agent requests vacation in its GC. The owner confirms. Changes are new versions; cancel is a status change, never a delete.
- **Links.** Optional `source_gc_id`, Vault doc and task references (12 links), all informational.

## Data and interfaces
```sql
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
interface IInterruptStack {
    Task<Frame> PushAsync(AgentId id, InterruptSource src, string reason, CancellationToken ct);
    Task<Frame?> PopAsync(AgentId id, FrameId frame, string resumeNote, CancellationToken ct); // idempotent
}
interface IOrgGraph { Task<IReadOnlyList<OrgEdge>> RoutesAsync(ProjectId p, AgentId a, CancellationToken ct); }
interface ICalendar { Task<CalendarEvent> SetAsync(CalendarDraft d, long? expectedVersion, CancellationToken ct); }
```

## Behaviour and edge cases
- A Constitution commit that fails to parse, or lacks a required heading, is rejected. The previous commit stays active; nothing is half-applied.
- Org: `reports_to` cycles within one scope are rejected on insert, under the same advisory lock pattern as 12. Ended loans are read-only.
- Interrupts: nesting depth 2 (an owner push over one system frame). A third push is refused and the owner is told. Pops are idempotent by frame id. Identical system pushes within 60 s collapse into one frame.
- Calendar: overlapping vacations are merged by the availability machine, not the table. Inverted ranges are refused. Cancelling a live vacation restores availability through 09 transitions.
- Security: only the owner's authenticated messages or system events create interrupts. Text from untrusted sources cannot push a frame.

## Testing
Real Postgres (xUnit v3 fixture) for constraints. Pure tests for routing and stack rules. Fake clock (`TimeProvider`) for TTL and DST. Fake gateway for interrupt timing at tool boundaries.
- Constitution: an accepted commit changes the excerpt from the next turn only; a running turn keeps its commit; no activation without an accepted commit; re-acknowledgement records the commit.
- Org: two `reports_to` in one scope refused; a cycle refused; a project item routes to the project lead, a company item to the owner; a loan ending leaves the card owner unchanged and posts a GC line.
- Interrupt: a push waits for the in-flight model call, then takes effect at the tool boundary; a pop resumes without repeating completed tools; an agent-sourced push is refused; an owner push during curfew wakes, a system push queues; a third nested push is refused; crash with two frames restores the top frame; card state unchanged.
- Calendar: vacation beats curfew; release events leave availability unchanged; DST day; cancel restores ON_SHIFT; all-day events across zones.
- `Avalonia.Headless`: org lens switch, calendar month chips, stack indicator on the presence chip.

## Work packages
1. Constitution section parser and accepted-commit check. Needs 18 WP4, WP9.
2. `constitution.changed` event, inbox item, excerpt refresh. Needs 1, 06 WP2, 07 WP2.
3. Org schema, unique and cycle constraints, SQL repositories. Needs Wave 1 migrations.
4. Org routing function and loan lifecycle with GC lines. Needs 3, 12 WP6.
5. Interrupt frame table and stack service. Needs 04 migrations.
6. Scheduler wiring at tool boundaries; resume. Needs 5, 06 WP3, WP5.
7. Calendar table, service, GC lines. Needs Wave 1 migrations.
8. Availability consumes calendar vacations. Needs 7, 09 WP3, WP9.
9. Client views: org, calendar, stack indicator. Needs 27, 2, 4, 5, 7.

Order: 1→2; 3→4; 5→6; 7→8; all → 9.

## Conflicts
1. **Constitution is v1 but undrafted, and onboarding is blocked on it** (07, Bazaar.md §4). No Soul can activate in v1 until the owner approves a commit.
2. **Interrupt contract change.** 06 lists owner messages, build-broken and security events as interrupt sources, and exposes `Interrupt(agentId, reason)`. This doc replaces it with `PushAsync`. 06 must adopt it.
3. **Calendar ownership.** 09 defines `calendar_event` as a feed. This doc owns the table; 09 must consume it.
4. **Owner override over vacation.** 09 says override bypasses vacation. A vacation can be a real absence; this needs owner judgment (Q9).
5. **Authority vs Council.** `reports_to` must never outweigh a vote (10). It routes approvals only.
6. **Cache cost.** The Constitution excerpt sits in the cached prefix (06); each change invalidates it once per agent.
7. **Privacy.** The Constitution is private company material. It must not enter the public platform repo (CLAUDE.md; 18 Conflicts 4).

## Questions for the owner
1. **Who drafts Constitution v1?** Default: you write a one-page seed (mission, vision, culture) in your own words; Claude drafts the other sections from DECIDED items; you approve one commit before any Soul activates. Why: those sections are your voice.
2. **Constitution approval.** Default: owner only in v1, including engineering principles.
3. **Re-acknowledgement.** Default: every accepted change, at next shift start.
4. **Excerpt budget.** Default: 1,500 tokens in the system prefix; full text read at onboarding.
5. **Org relation kinds.** Default: `reports_to` and `lent_to` only; covers stay in 09.
6. **Who approves a loan?** Default: you, or a project lead you name; agents request in a GC.
7. **Approval routing across projects.** Default: project items to that project's lead; company items to you.
8. **Who may interrupt?** Default: you, for any agent at any time, plus build-broken and security events. Agents never interrupt agents.
9. **Does your emergency override bypass vacation?** Default: curfew and break yes; vacation only for interrupts you tag `emergency`; provider caps never cleared.
10. **Stack depth.** Default: 2. A third push is refused.
11. **Suspended cards.** Default: keep the claim for 4 working hours, then a GC prompt asks pause or release.
12. **Availability-changing kinds.** Default: vacation only (09 Q11).
13. **Who creates vacations and release events?** Default: you confirm vacations, agents may request them; release and milestone events are created by you or a project lead.
14. **Calendar time zone.** Default: viewer's zone, with the agent's zone shown on hover.
