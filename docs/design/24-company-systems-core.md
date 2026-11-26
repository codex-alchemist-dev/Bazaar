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
- **RyuseiFuda [PROPOSED, doc 38].** RyuseiFuda fires are not interrupts by default. The owner emergency path (tag `emergency`) is the only bypass; `ryusei.bypass_curfew` is off by default.
- **Kanban [PROPOSED].** An interrupt never changes card state. The suspended card keeps its claim for `interrupt.suspended_claim_hours` working hours, then the service posts a GC prompt: pause or release.
- **Recovery [PROPOSED].** The stack is rebuilt from the frame table after a crash, never from memory.
- **Presence [PROPOSED].** The stack indicator sits on the presence chip. Labels and overlays follow the DECISIONS-LOG round 11 (C43) list; adding a label needs the owner.
- **Owner producer [PROPOSED, 2026-11-22].** The owner pushes and pops through two owner-only HTTP routes served by `Bazaar.Server.Interrupts` and mapped by the host behind the HTTP surface guard (bearer, rate bucket) plus an owner check: the request's principal must be the company owner, and anything else, an agent bearer or an unresolved caller included, gets `403`. Because only an authenticated owner request can push, untrusted text can never push (C06): the route reads no GC, mail or web content, and the reason is what the owner typed. `POST /company/interrupts/push` takes `{ agentId, reason, gcId, emergency?, ttlSeconds? }` (reason 1 to 500 characters; `emergency` defaults to false; `ttlSeconds` 1 to 604800, default no expiry) and answers `200 { frame: {id, agentId, source, state, pushedAt}, wakeBypassesVacation }`. The pusher's person id is the author the reason is sealed under. `wakeBypassesVacation` is `InterruptWakePolicy.BypassesVacation(Owner, emergency)`, handed to the runtime and schedule (doc 09). The tag is stored on the frame (PROPOSED, 2026-11-22): `interrupt_frame.emergency boolean NOT NULL DEFAULT false` (migration 0002 in the `interrupts` namespace), `InterruptFrame.Emergency`, a trailing `emergency` argument on `PushAsync`, and `frame.emergency` in the push answer. Only an owner frame can carry it: the service stores `false` for a system source. The read model of `POST /company/interrupts` is unchanged. A third push, with two frames already live, answers `409 { error: "depth_exceeded" }` and changes nothing. `POST /company/interrupts/pop` takes `{ agentId, frameId }` and answers `200 { resumed: frame|null }`; popping a frame that is already done or cancelled is a no-op that answers `200` with `resumed: null` (idempotent by frame id); popping a suspended frame answers `409 { error: "not_top" }`. A body that is not a JSON object, has a missing or malformed field, or exceeds 4 KB answers `400 { error: "bad_request" }`. Sources, GC line and runtime effects are as above; the routes only write the frame.
- **System producer [PROPOSED, 2026-11-22].** System frames (`system_build`, `system_security`) are written by in-process callers of `IInterruptStackService.PushAsync`; the service already collapses identical pushes within 60 seconds and seals under the company owner. No producer ships yet. Checked 2026-11-22: (1) Kodo (doc 46) has only a pure core. Its `kodo` stream writer and webhooks have no host wiring, no in-process subscription seam and no polling pattern, and its states describe host and module health, not a broken build, so a red Kodo state is not a build-broken signal. (2) Security taint (doc 15) is per-session state read through `IAgentSessionTaintReader`; the module raises no taint or incident event (only sign-in and package-request status listeners exist). (3) The Health module reports `build_failure_streak` only as a pulled report (doc 25). Rather than invent a seam, the producer waits for one: a build-broken signal (for example a Health or CI event, not Kodo's host state) and a taint-raised or incident-opened event from the server's own tracker, never from message content. Which agents a system frame targets is set below. When it lands it targets the agent set in the setting `interrupt.system_targets` (enum `none`, `project_members`, `all_agents`; default `none`, operator, audit entry), and a security event follows the schedule exceptions above. Until then the `none` default is the only behaviour.
- **Build-broken seam [PROPOSED, 2026-11-22].** `Bazaar.Server.Health.Abstractions` offers `IBuildBrokenListener` (event `BuildBroken(projectId, runRef, at)`). The Health collection job (doc 25) reads CI run facts, and a small in-process detector raises the event once per red streak: the first failed run after a passed one. The first pass over a project only records a baseline and raises nothing; the baseline is in memory, so a break that happens while the server is down is not raised. Listeners are called after the samples are stored; a listener that throws is logged and never fails collection. It carries no message text. The Interrupts producer (below) is one listener.
- **System producer wiring [PROPOSED, 2026-11-22].** `Bazaar.Server.Interrupts` registers one in-process listener for both seams: `IBuildBrokenListener` (above) and `ITaintRaisedListener` (doc 15, "Taint raised event"). It reads `interrupt.system_targets` through a small reader port the host implements over the settings store, and does nothing when the value is `none` (the default). Reasons are fixed server text built from ids (`Build broken in project <id> (run <ref>)`, `Session taint raised to hard`), never message text. The frame's GC is the GC in the agent's presence row (doc 23); an agent with none falls back to the company's general GC, the one the first-run seed made (doc 45 section 1.15), read through `IGeneralGcReader` [PROPOSED, 2026-11-22]. Only when there is neither (no seed, or the general GC archived) does the agent get no frame and a log line, because a frame needs a GC to be announced in. The 60-second collapse of identical pushes is the service's. Targets: `all_agents` is every non-retired agent from `IAgentDirectoryReader`. `project_members` has no production membership source yet (the Vault's `IProjectMembership` is not registered and Health's project id is not tied to a slug), so until one exists it pushes to nobody and logs once; this is the only reading that does not invent spec. A build-broken frame goes to every target. A taint frame goes only to the tainted agent, and only when it is a target, and only for a rise to hard: a soft rise, or a frame to every agent for one agent's taint, would flood the stacks [PROPOSED, owner to confirm].

### 4. Company Calendar
- **Ownership [DECIDED, C45].** This doc owns `calendar_event`. The calendar writes no availability. The 09 availability machine is the single writer and reads events with `affects_availability`.
- **Kinds [PROPOSED].** vacation (flagged `affects_availability`); planning, release, freeze, milestone (informational). Which kinds change availability is doc 09's `schedule.calendar_availability_kinds`; v1 default is vacation only.
- **Requests [PROPOSED].** An agent requests vacation in its GC. Confirmation follows `calendar.vacation_confirmer`. Release and milestone events are created as set by `calendar.release_creators`. Changes are new versions; cancel is a status change, never a delete.
- **Links [PROPOSED].** Optional `source_gc_id`, Vault doc and task references (12), all informational.
- **Personal fields [PROPOSED].** `title` and any vacation note are personal fields under C01: stored as per-person encrypted payloads (04).

### 5. Client views (WP9) [PROPOSED, 2026-11-18]
Three client modules, each a pair `Bazaar.Client.<Name>` plus `Bazaar.Client.<Name>.Abstractions` (41b §2a). None of them adds code to `Bazaar.Client.Core`; each registers through `IPaneModule` and a head lists it, as `Bazaar.Client.RyuseiFuda` does (doc 40 step 7). The server halves (`Bazaar.Server.Org`, `Bazaar.Server.Calendar`, `Bazaar.Server.Interrupts`, from the extraction PRs) send ordinary pane descriptors; the client reads the JSON model and never sees SQL shapes. All three are read-only in v1: no client edits an edge, an event or a frame, because who may edit is `org.relation_approver`, `calendar.vacation_confirmer` and the owner-only interrupt rule, and those run server-side.
- **Org (`Bazaar.Client.Org`, pane kind `bazaar.org`).** Model: `{ "projects": [{id, name}], "agents": [{id, name}], "edges": [{id, kind, fromAgentId, toAgentId, projectId|null, startsAt, endsAt|null}] }`. A lens switch picks Company (edges with a null project) or one project. `reports_to` draws as a tree; `lent_to` draws as a loan row with its end date; ended loans are shown dimmed and read-only. The view tolerates a cycle or a missing agent (the server rejects cycles, but the view must never hang on bad data): it draws each agent once and marks the repeat. A malformed model raises `PaneModelException`, so the registry falls back to the generic view. The pane states that edges grant no authority.
- **Calendar (`Bazaar.Client.Calendar`, pane kind `bazaar.calendar`).** Model: `{ "displayTimezone": "viewer"|"agent", "events": [{id, kind, title, startsAt, endsAt, allDay, tz, agentId|null, projectId|null, affectsAvailability, status}] }`. A month grid with one chip per event per day; kinds vacation, planning, release, freeze, milestone; `cancelled` events are struck through, never hidden. Time zone follows `calendar.display_timezone` (default `viewer`, a client preference), resolved through an injected `TimeProvider` and zone lookup so tests fix the zone. An all-day event stays on its own calendar date in every zone; a timed event lands on the viewer's local date; a DST day has 23 or 25 hours and the grid does not drift. Titles and vacation notes are personal fields (C01): the model carries them already decrypted for this viewer, and the client never logs them.
- **Interrupt stack (`Bazaar.Client.Interrupts`).** A stack indicator, depth 0 to 2: a view model plus a small Avalonia control, and a pane kind `bazaar.interrupt-stack` listing agents whose stack is not empty (top frame source `owner`, `system_build` or `system_security`, reason, pushed time, `suspended` count). Model: `{ "agents": [{agentId, name, frames: [{id, source, reason, state, pushedAt}]}] }`. The indicator belongs on the presence chip (C43), but the chip lives in the GC client module, which does not exist yet (`Bazaar.Client.GC`, 41b §2a); until it does, the indicator shows in its own pane and the chip host adopts the control later. Colour never carries the meaning alone: depth and source are text for screen readers (doc 27 accessibility). No new presence label is added (C43 needs the owner).
- **Model conventions [PROPOSED].** For an `org_relation`, `fromAgentId` is the report (or the lent agent) and `toAgentId` is the manager (or the borrowing project's lead). An all-day event's `startsAt` and `endsAt` are both inclusive calendar dates; a timed event ending exactly at local midnight does not cover the next day. A model with more than two live interrupt frames is bad data: the client shows depth 2 with a visible "stack over limit" note. Timestamps without an offset are read as UTC.
- **Abstractions.** Each `.Abstractions` project holds the immutable model records the view models take and one optional source interface (`IOrgSnapshotSource`, `ICalendarSnapshotSource`, `IInterruptStackSource`) a head may supply through `PaneModuleContext.With<T>()` to refresh a pane without a new descriptor. They reference only `Bazaar.Contracts`.
- **Read routes [PROPOSED, 2026-11-22].** The server serves each pane model over HTTP so a head can refresh a pane without new pane data: `POST /company/org` (body `{}`), `POST /company/calendar` (body `{}` for the server's default window, the current month and the month either side; optional `from` and `to` dates, both inclusive, narrow it) and `POST /company/interrupts` (body `{}`). Each answers `200` with `application/json`: exactly the pane model above (camelCase, same field names), filtered to what the caller may see (private vacation notes and titles follow the C01 personal-field rules). A non-200 answer makes the source throw `HttpRequestException`, and a body the reader rejects makes it throw `PaneModelException`; the caller treats any exception as no data and the pane keeps what it shows. The route handlers are server modules (`Bazaar.Server.<Name>`), not `Bazaar.Server`. Each client module ships an `Http<Name>Source` that implements its source interface over these routes with the head's `ServerEndpoint`; the head registers it through `PaneModuleContext.With<T>()`. Pull, not push: a push channel for stack changes waits on the doc 41 section on live panes.
- **Interrupt reasons on the read route [PROPOSED, 2026-11-22].** A frame's `reason` is a personal field (C01). `POST /company/interrupts` sends it only to the company owner and, for that agent's own frames, to the agent itself (the agent is told the reason when the frame is pushed). Every other viewer, and a caller the server cannot identify, gets the frame with `reason` replaced by `(private)`; the id, source, state and pushed time stay, so the stack depth and the source remain visible. The viewer comes from the request's bearer, and anything that does not resolve is the least-privileged viewer.
- **Interrupt reason sealing [PROPOSED, owner decision 2026-11-22].** A reason is sealed with the pusher's per-person key through `IGcBodyCodec` (doc 01 WP6), the same seam Calendar titles use. An owner push is sealed under the person who pushed it. A system push (`system_build`, `system_security`) has no person, so it is sealed under the company owner's person key (the owner reads all). Erasing a person destroys their key, and the reasons they wrote then open as `[erased]`; the frame stays. `IInterruptStackService.PushAsync` takes the author's id for this; the service replaces it with the company owner for system sources. Because the codec binds a value to its key id rather than to the row, a sealed reason is not row-bound. Sealing and opening are async, since the key is read through the database.
- **Calendar event visibility [PROPOSED, 2026-11-22].** Each calendar event carries `visibility`: `public` (everyone sees the title), `attendees` (only attendees, the owner and the company owner see the title; others see the time block with the title redacted to `(private)`) or `owner` (only the event's owner and the company owner see the title; others see `(private)`). Default `public` for company events and `attendees` for personal events such as vacation. The read route applies this per caller; the time block is always shown so availability stays honest.
- **Org directory [PROPOSED, 2026-11-22].** The Org read route takes agent names, roles and project names from the Agents module abstractions through a read-only `IOrgDirectory` seam, not from the Org module's own storage. Unlisted agents still render with their id.
- **Org directory fields [PROPOSED, 2026-11-22].** The agent record gains a nullable `role` text column (free text, for example `Engineer`; owned by the Agents module, set by the owner or the agent's profile, empty by default). A board is today only a `projectId` with no name, so the Kanban module gains a small `kanban_project` table (`project_id`, `name`; migration in the Kanban module's own namespace) and a read-only `IBoardDirectoryReader` seam in `Bazaar.RyuseiFuda.Abstractions` that lists boards with names; a board that has tasks but no name row is listed as `Project <first 8 hex digits of the id>`. The Org module reads that seam. A full project registry, if one is designed later, replaces this table. Both feed `IOrgDirectory`.
- **Agent model label [PROPOSED, 2026-11-22].** The agent record also gains a nullable `model` text column (agents migration 0003): the short label of the model the agent runs on, such as `Haiku 5.5`, shown as the model badge on the profile card (doc 07 read-only profile route). It is a display label only; routing and the model whitelist stay in `agent_model_policy` (doc 05). First-run setup sets it when it seeds the first agent, from the first Claude route on its whitelist (`claude-<family>-<major>-<minor>` becomes `Family major.minor`); a whitelist with no Claude route leaves it empty, never a guess. Later changes to an agent's model are the Agents module's to make; this section adds no editor.
- **Pane kinds** are added to `PaneKinds` in `Bazaar.Contracts` as `bazaar.org`, `bazaar.calendar` and `bazaar.interrupt-stack` (shared contract, not a feature).
- **Tests.** Each module has its own test project: model parsing (good, malformed, unknown fields), lens and grid logic as pure tests, and an `Avalonia.Headless` test per pane (org lens switch, calendar month chips, stack indicator).

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
// As built in INT1 (migration 0015, src/Bazaar.Server/Interrupts). resumeNote and the GC line move to WP6; the emergency tag is stored from migration interrupts/0002 (PROPOSED).
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
| `interrupt.system_targets` [PROPOSED] | enum: `none`, `project_members`, `all_agents` | `none` | install | operator | no; audit entry |
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
5. Interrupt frame table and stack service. Needs 04 migrations. **Done in INT1:** migration 0015 and `IInterruptStackService`, with a real-Postgres test suite. Not yet done: GC lines, the cancel flag, `resume_turn_id`, and the cancel flag (WP6). The emergency tag is PROPOSED in interrupts/0002.
6. Scheduler wiring at tool boundaries; resume. Needs 5, 06 WP3, WP5.
7. Calendar table, service, GC lines. Needs the 04 core migrations.
8. Availability consumes calendar vacations. Needs 7, 09 WP3, WP9.
9. Client views: org, calendar, stack indicator, as the three module pairs in section 5. Needs 27, 2, 4, 5, 7. Client side PROPOSED 2026-11-18; code follows the docs PR.

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
