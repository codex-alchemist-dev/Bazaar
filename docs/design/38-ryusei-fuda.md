# RyuseiFuda: the three-layer planner

Status: PROPOSED (planning draft, 2026-11-08). Owner direction from the thread "Design RyuseiFuda three-layer Kanban" (Dom-tron). Safe defaults are recorded in every section, so the design is complete without a reply. Nothing here is built until the owner accepts it. Layer 1 is the existing board in doc 12 and stays DECIDED where doc 12 says so.
Depends on: `01-gc-data-model.md` (GC, system lines, membership), `04-postgres-schema-and-migrations.md` (migrations, NOTIFY, append-only triggers), `06-agent-runtime.md` (inbox, wake reasons, loop guards), `09-work-life-simulation.md` (schedules, curfew), `12-kanban-and-dependencies.md` (task service, the board, `kanban.*` settings), `15-security-taint-and-approvals.md` (taint, approval tiers, audit), `18-vault.md` (VaultWriter, front matter, `vault_docs`), `19-constellation-and-git-solar-system.md` (graph projection, node kinds), `21-github-integration.md` (PR events), `03-server-host-and-providers.md` (settings registry, job runner). Used by: `23-observatory-and-usage.md` (trigger and fire events), `24-company-systems-core.md` (Interrupt System, Calendar), `26-fun-systems.md` (Psych Ward), `27-client-architecture.md`, `28-windows-client.md`, `29-android-client.md` (panes).

## Purpose
RyuseiFuda ("shooting-star cards", the umbrella name for the company planner) is one planning surface with three layers:

1. **Macro board (company-wide).** The readable schedule of the dev team and the general tasking and workflow board. This is today's Kanban (doc 12) unchanged in rules.
2. **Micro schedule (per agent).** Physical scheduling: each agent, or a coworker, can queue a wake that re-invokes an agent later. The trigger is a timer, an interval, a watcher on a condition, or a one-shot event. This is the RyuseiFuda equivalent of Claude's self-reinvoke and scheduled wakeups.
3. **Objective roadmap.** Project planning, documentation and dev phasing. It works closely with the Constellation: objectives are Vault documents, and the Constellation draws them as a phase graph.

The layers stay separate on purpose. The macro board says what work exists and what waits on what. The micro schedule says when an agent is woken and why. The roadmap says what the work is for and in what order it lands. Each layer has one writer and one source of truth, so none can silently change another.

## Decided inputs
- Kanban is a dependency board with five stored columns; Blocked is derived; the task service is the only writer of task state (doc 12; DECISIONS-LOG round 1, C28).
- GCs are the coordination system. Events and triggers serve GCs and never replace them. Every trigger action that speaks to others is a GC line (`CLAUDE.md`; round 1, C09).
- Agents never write task tables directly. Board moves go through `kanban.transition`, which calls the task service (doc 12, interception).
- Board moves are not outward high-impact steps, so hard taint does not gate them (round 2, C06). Done on a PR card means the PR is merged (round 8, C28).
- Vault Markdown in git is the source of truth; Postgres only indexes it. Writes go through `VaultWriter` (doc 18; round 7).
- Constellation owns no tables. It defines views over tables other docs own (doc 19; round 6, C16).
- Per-agent schedules and timezones come from doc 09. Wake reasons and the inbox come from doc 06 (round 3, C08). Doc 06 lets other docs add wake kinds, but not change its keys.
- Settings are a registry in doc 03. Floors and security invariants are not settings (round 13).
- Triggers have no authority of their own: a trigger wakes an agent, and the agent acts under its own permissions (doc 15). This follows the taint and approval model; it is not a new rule.

## Naming

**RyuseiFuda** (流星札) is the name the owner coined for the company planner. It means **"shooting star ticket"**: *ryusei* (流星) is a meteor or shooting star, and *fuda* (札) is a card, ticket or tag. The imagery is deliberate: a ticket streaks across time and re-invokes an AI at the moment it is due.

- **Spelling.** Write **RyuseiFuda** (one word, capitals at the joins). The owner also wrote "Ryusei-fuda" and "RuiseiFuda". Treat all three as the same name. "RuiseiFuda" is a misspelling of the Japanese reading, not a second name. Search and aliases accept all three; the written form is always RyuseiFuda.
- **Scope.** RyuseiFuda is the umbrella for the three layers (macro board, micro schedule, objective roadmap). It is also the formal answer to the agent loop and scheduling question. Earlier design notes said "GC and Kanban handle that"; that is superseded. Wake scheduling, re-invocation, watchers and loop limits for agent scheduling are owned by RyuseiFuda (this doc). GCs stay the place where agents talk, and events stay the software's reactions (`Bazaar.md` §3).
- **Layer names.** Macro board (company-wide, the readable team schedule; the current Kanban, doc 12), micro schedule (per-agent triggers and re-invocation, this doc), objective roadmap (Vault objectives and phases, Constellation view, this doc).
- **Use in the docs.** Glossary (`Bazaar.md` §4) and every design doc that touches scheduling or agent loops say RyuseiFuda. Doc titles and UI say RyuseiFuda; the macro layer adds the subtitle "Board" in the UI ("RyuseiFuda · Board").
- **Code identifiers.** New code uses `Ryusei` (for example `RyuseiTrigger`, `ryusei_trigger`, component ID `ryusei-schedule`). Existing `Kanban` identifiers, IDs and settings keys (`kanban.*`) stay until the optional rename step R4 below, because renaming them now would churn stored data and contracts for no behaviour change.

## Layer 1: macro board (company-wide)

### What stays the same
Everything in doc 12: the five columns, the transition table, derived Blocked, PR-card rules, gates, reopen, auto-Ready, the settings table, and the task service. No schema change is needed for layer 1.

### What is added
- **Team schedule view.** A read-only view of tasks grouped by owner, ordered by `due_at`, with blockers drawn as chips. It uses only existing columns (`owner_agent_id`, `due_at`, `state`, `task_edges`). Default: no new start-date column in v1. If owners want a start date, that is one column in a later migration.
- **Micro links on a card.** A task card lists the micro triggers that reference it (from layer 2). Triggers never change task state. A trigger that should move a card calls `kanban.transition`, which goes through the same guard as any agent (doc 12).

## Layer 2: micro schedule (per agent)

### Trigger types
The set is closed in v1. Each trigger has exactly one type.

| Type | Fires when | Spec (stored in `spec` jsonb) | Notes |
|---|---|---|---|
| `timer` | Once, at `fire_at` (UTC) | `{ "fireAt": "..." }` | One-shot. Equivalent to a scheduled wakeup. |
| `interval` | Every `every` duration, from `anchor` | `{ "every": "PT30M", "anchor": "..." }` | Recurring. Missed runs coalesce into one fire (no burst). Floor on `every` from settings. |
| `watcher` | A predicate over the event stream holds | `{ "predicate": {...}, "pollSeconds": 60, "debounceSeconds": 30 }` | Repeats until `maxFires` or `expiresAt`. Closed predicate set (below). |
| `event` | A named event fires once, with an optional filter | `{ "eventKind": "task.transitioned", "filter": {...} }` | One-shot. Same predicate set as watchers, with no polling. |

**Closed predicate set (v1):** task state change (`task.transitioned`, `task.due_passed`, `task.blocked`, `task.unblocked`); PR event (`pr.merged`, `pr.closed`, `pr.review_requested`, from doc 21); new GC message in a named GC, optionally from a named author; Vault document changed by path glob (doc 18); council decision recorded (doc 10). Each predicate names the event kind it reads. Anything else is refused at creation.

**Deferred:** cron expressions, free-text matching of GC messages, and CI status beyond the PR events. Each needs its own design, mainly for timezone handling and injection risk.

### Ping, attach and new-GC semantics
Every trigger has a `ping_mode`:

- `none`: the target agent is woken directly with the trigger's purpose text. No GC line.
- `attach` (default when `attach_gc_id` is set): the server posts one **system line** in that GC ("RyuseiFuda trigger `<id>` fired; pinging A, B"). Each target gets an inbox item keyed by that line's `(gc_id, gc_seq)`. The GC is the record, and doc 06's existing unique key deduplicates the wakes.
- `new_gc`: the server creates a GC named from the trigger purpose. Members are the targets and the creator. The owner reads all GCs (doc 01), so the owner is not added automatically. The creation and the first system line are the GC record.

Ping fan-out is capped at `ryusei.max_ping_fanout` (default 8). A trigger with more targets is refused at creation, not at fire time.

### Authority
- **An agent** may create, pause, resume and cancel triggers that target **itself**.
- **An agent** may create a trigger that targets a **coworker in the same project**. The coworker sees the trigger in its list and may pause it. Default: allowed, with a GC line naming the creator. (Settings: `ryusei.coworker_triggers` = `allowed`, `ask_first`, or `off`; default `allowed`.)
- **An agent** cannot target an agent in another project, or an agent in a retired or resting state.
- **The owner** may create, pause or cancel any trigger, and sees every trigger and fire.
- **The target agent** may always pause a trigger aimed at itself. Pausing is never refused.
- A trigger's fired turn runs under the **target** agent's permissions. The creator's permissions are not inherited, and a trigger cannot grant a tool.

### Firing and the work-life schedule
- A fire enqueues an inbox item (doc 06). It does not start a turn directly, so the normal wake priority and digest apply.
- Doc 09 applies: a fire during curfew or a break waits for the next shift. The owner's emergency path (doc 24, `emergency` tag) is the only bypass, and it is off for triggers by default (`ryusei.bypass_curfew` = false).
- A fire for an agent in **Resting** or **Retired** state (doc 06) is recorded as `skipped` and does not wait.
- If the payload comes from external content (a PR comment, an email, a webhook), the fired turn starts as a reader session under doc 15's hard-taint rule. Trigger purpose text written by the agent is soft-tainted, not hard.

### Missed fires and restarts
A fire missed while the server was down is coalesced into one fire on resume, with `missedCount` in the payload. An `interval` never bursts to catch up. A `timer` in the past fires once.

## Loop and runaway safeguards
Every safeguard below has a floor. Settings can tighten the value, never loosen it past the floor shown.

| Guard | Default (setting) | Floor | On trip |
|---|---|---|---|
| Interval floor (`every`) | 5 min (`ryusei.min_interval`) | 1 min | Creation refused |
| Active triggers per agent | 50 (`ryusei.max_triggers_per_agent`) | none | Creation refused with the count |
| Fires per trigger per 24 h | 96 (`ryusei.max_fires_per_day`) | none | Fire recorded as `refused_cap`; trigger paused and the owner notified |
| Chain depth (a trigger created from a triggered turn) | 3 (`ryusei.max_chain_depth`) | 1 | Creation refused; repeated refusal counts toward a loop trip |
| Cooldown between fires of one trigger | 60 s (`ryusei.cooldown_seconds`) | 10 s | Fire recorded as `refused_cooldown` |
| Single flight (one fire in flight per trigger) | on | on (invariant) | Fire recorded as `skipped_inflight` |
| Wakes per agent per hour (shared with doc 06) | 30 (`runtime.guard.wakes_per_hour`) | none | Fire recorded as `refused_cap`; trigger is not lost |
| Consecutive refusals before auto-pause | 3 (`ryusei.auto_pause_after`) | 1 | Trigger paused; GC line; owner notified |
| Ping-pong across triggers | doc 06 `runtime.guard.pingpong_limit` | — | Agents go to the Psych Ward (doc 26) |

Notes:
- The **Psych Ward** is the place for a loop trip: a ping-pong between two agents through triggers is the same loop doc 06 already guards against, and it uses the same path.
- A refused fire is never dropped silently. It is a row in `ryusei_fire` with its reason, and it appears in the trigger's history.
- Creation is idempotent per `(created_by, purpose_hash, spec)` within one minute, so a retried tool call does not multiply triggers.

## Authority, audit and taint
- **Audit.** Every create, update, pause, resume, cancel, fire and refusal is written to the audit log (doc 15) with the actor. The fire table is the operational record; the audit log is the security record.
- **GC lines.** A trigger created in a GC, a ping, and an auto-pause each write one GC line. A fire with no GC is visible in the trigger's history and in Observatory (doc 23).
- **Security.** Triggers are rows that only the trigger service writes. Injected text can change a trigger only by making an agent call the tool, which the normal tool rules govern (doc 15). A trigger payload from external content is tainted (see Firing).
- **Board changes.** A trigger that moves a card calls the task service. No trigger has a direct write path to task tables.

## Layer 3: objective roadmap (with the Constellation)

### Objectives are Vault documents
Objectives and phases are Markdown files in the Vault, written only through `VaultWriter`. The Constellation and the search index read them. Postgres indexes them; it does not own them.

```
roadmap/phases/<phase-slug>.md          kind: phase
roadmap/objectives/<objective-slug>.md  kind: objective
```

Front matter (the schema is fixed here; the Vault indexer reads it):

```yaml
kind: objective            # or phase
phase: <phase-slug>        # objective only; wiki-link to a phase doc
status: planned            # planned | active | done | dropped
depends_on: [ "[[objective-slug]]", ... ]   # objective-to-objective edges
owner: <agent or owner id>
```

Links from objectives to tasks use the existing `task_links` kind `vault_doc` (doc 12). A task that advances an objective links to the objective's path. No new link kind is needed.

### Derived views (no new tables)
Views over `vault_docs` and `vault_links` (doc 18) plus `task_links` and `tasks` (doc 12):

- `roadmap_objectives`: objective rows with phase, status and dependency edges from front matter.
- `roadmap_progress`: per objective, the count of linked tasks by state, and the share in Done. Progress is derived; it is never stored.
- `roadmap_phase_gate`: whether every objective in a phase is Done or dropped.

### Who changes the roadmap
- The owner, or a Council decision (doc 10), changes an objective's `status` or a phase's membership.
- An agent may propose a change in a GC. The proposal is a message, not a write.
- Default `depends_on` edits: owner only. (Cycle checks reuse the doc 12 graph rule.)

### Constellation
Doc 19 gains two node kinds, `objective` and `phase`, and one edge kind, `depends_on`. Layout is the existing server-side graph layout. A phase is a cluster. Progress is shown as a ring from `roadmap_progress`. No client simulation (doc 19 compute rule).

## Data model (proposed)

Layer 1 uses the existing tables from doc 12 (`tasks`, `task_edges`, `task_links`, `task_events`, `task_gate_decisions`). Layer 2 adds two tables, in a new migration namespace `ryusei` (next free number under the component's own folder; see Migration). Layer 3 adds only views.

```sql
-- Migration: ryusei/0001_triggers.sql (proposed; owner: WP-R3)
CREATE TABLE ryusei_trigger (
    trigger_id           uuid        PRIMARY KEY DEFAULT uuidv7(),
    project_id           uuid        NOT NULL,
    created_by           uuid        NOT NULL,
    kind                 text        NOT NULL CHECK (kind IN ('timer', 'interval', 'watcher', 'event')),
    target_agent_ids     uuid[]      NOT NULL CHECK (cardinality(target_agent_ids) BETWEEN 1 AND 8),
    purpose              text        NOT NULL CHECK (btrim(purpose) <> ''),
    spec                 jsonb       NOT NULL,
    task_id              uuid        REFERENCES tasks (id),
    attach_gc_id         uuid        REFERENCES gc (gc_id),
    ping_mode            text        NOT NULL CHECK (ping_mode IN ('none', 'attach', 'new_gc')),
    state                text        NOT NULL CHECK (state IN ('active', 'paused', 'fired', 'expired', 'cancelled')),
    next_fire_at         timestamptz,
    fire_count           bigint      NOT NULL DEFAULT 0,
    consecutive_refusals int         NOT NULL DEFAULT 0,
    max_fires            bigint,
    expires_at           timestamptz,
    chain_depth          int         NOT NULL DEFAULT 0 CHECK (chain_depth BETWEEN 0 AND 3),
    version              bigint      NOT NULL DEFAULT 1,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CHECK ((ping_mode = 'attach') = (attach_gc_id IS NOT NULL) OR ping_mode <> 'attach')
);
CREATE INDEX ryusei_trigger_due_idx ON ryusei_trigger (next_fire_at) WHERE state = 'active';

-- Append-only record of every fire, including refusals and skips.
CREATE TABLE ryusei_fire (
    fire_id       uuid        PRIMARY KEY DEFAULT uuidv7(),
    trigger_id    uuid        NOT NULL REFERENCES ryusei_trigger (trigger_id),
    scheduled_for timestamptz NOT NULL,
    outcome       text        NOT NULL CHECK (outcome IN ('woke', 'coalesced', 'refused_cap', 'refused_cooldown',
                                                          'refused_loop', 'skipped_inflight', 'skipped_resting', 'error')),
    cause         jsonb,
    gc_seq        bigint,           -- set when ping_mode is attach or new_gc (the system line)
    at            timestamptz NOT NULL DEFAULT now(),
    UNIQUE (trigger_id, scheduled_for)
);
CREATE TRIGGER ryusei_fire_append_only BEFORE UPDATE OR DELETE ON ryusei_fire
    FOR EACH ROW EXECUTE FUNCTION gc_append_only();
CREATE TRIGGER ryusei_fire_no_truncate BEFORE TRUNCATE ON ryusei_fire
    FOR EACH STATEMENT EXECUTE FUNCTION gc_append_only();
```

The `UNIQUE (trigger_id, scheduled_for)` constraint makes a crash-retried fire a no-op. The scheduler claims due rows with `FOR UPDATE SKIP LOCKED`, and the job runner from doc 03 drives it (job kind `ryusei.trigger_tick`).

Note the CHECK on `attach_gc_id` is written loosely above; WP-R3 must state it as "`attach_gc_id` is set exactly when `ping_mode = 'attach'`" and test both directions.

### Wake reasons (doc 06)
Layer 2 adds these wake kinds: `ryusei_timer`, `ryusei_interval`, `ryusei_watcher`, `ryusei_event`, `ryusei_ping`. Doc 06 allows other docs to add kinds (round 3).

**Shape conflict for doc 06:** its inbox unique key `(agent_id, gc_id, gc_seq, kind)` assumes every wake comes from a GC line. A trigger fire with `ping_mode = none` has no GC line. Default (proposed to doc 06): the key becomes `(agent_id, source_key, kind)`, where `source_key` is the GC line's `gc_seq` or the `fire_id`, so both cases dedupe. This is a shape change, so it is one answer for doc 06's owner, not a setting.

## Settings (doc 03 registry)

| Key | Type | Default | Scope | Who may change | Approval |
|---|---|---|---|---|---|
| `ryusei.min_interval` | ISO duration | PT5M (floor PT1M) | install | operator | No; audit |
| `ryusei.max_triggers_per_agent` | int | 50 | install | operator | No; audit |
| `ryusei.max_fires_per_day` | int | 96 | install | operator | No; audit |
| `ryusei.max_chain_depth` | int, 1 to 3 | 3 | install | operator | Yes if raised |
| `ryusei.cooldown_seconds` | int, 10 to 3600 | 60 | install | operator | No; audit |
| `ryusei.auto_pause_after` | int, 1 to 20 | 3 | install | operator | No; audit |
| `ryusei.max_ping_fanout` | int, 1 to 8 | 8 | install | operator | Yes if raised |
| `ryusei.coworker_triggers` | enum: `allowed`, `ask_first`, `off` | `allowed` | project | project owner | No; GC line |
| `ryusei.ping_mode_default` | enum: `none`, `attach`, `new_gc` | `attach` when a GC is given, else `none` | project | project owner | No |
| `ryusei.bypass_curfew` | bool | false | install | operator | Yes (owner) |
| `ryusei.poll_seconds` | int | 60 (floor 10) | install | operator | No |
| `ryusei.debounce_seconds` | int | 30 | install | operator | No |

Settings may tighten a floor. They cannot remove the single-flight rule, the audit of fires, or the rule that a trigger has no authority of its own.

## Migration and rename plan

The technical IDs stay stable. Only the user-facing name changes, so no client or stored data breaks.

| Step | What changes | What does not change | When |
|---|---|---|---|
| R0 | This doc. Index row in `docs/design/README.md`. A pointer line in doc 12's status (Layer 1 of RyuseiFuda). | Code, wire, schema | This PR |
| R1 | Pane and window titles read "RyuseiFuda · Board". `KanbanPaneViewModel` title comes from the pane descriptor. | Pane ID `kanban`; board stream; column names | WP-R11 |
| R2 | Layer 2 migration, service and client pane. Component `ryusei-schedule` (kind Feature) depends on `kanban`. | `kanban` component, tables, settings | WP-R3 to R9 |
| R3 | Layer 3 views, Vault front-matter schema (doc 18), Constellation node kinds (doc 19). | Vault layout for existing docs | WP-R12 |
| R4 (optional, later) | Code namespace and project rename (`Bazaar.Server.Components.Kanban` to a RyuseiFuda name). Only if the owner wants it. | Everything else | Not scheduled |

Doc 12's `kanban.*` settings keep their names. Doc 12 is not renamed, because its settings, stored data and wire contracts already depend on the ID.

Component IDs (doc BAZAAR-COMPONENTS, rule: IDs stable across versions): `kanban` (layer 1, unchanged); `ryusei-schedule` (layer 2, new); `ryusei-roadmap` (layer 3, new, mostly views and a pane).

## Behaviour and edge cases
- **A trigger whose task is Done.** Still fires for the purpose it describes. Default: the trigger does not auto-cancel; it is paused by the owner or by a watcher's `expiresAt`.
- **A trigger whose target is retired.** Fire is recorded `skipped_resting`, and the trigger moves to `expired`.
- **Two triggers that wake one agent in the same second.** Each is a separate inbox item with its own source key; doc 06's digest and priority order apply.
- **A new GC for `new_gc` mode fails to create** (for example, the project's GC limit). The fire is `error`, is retried on the next tick up to 3 times, and then pauses the trigger with a notice.
- **Clock and timezone.** Fire times are stored in UTC. `interval` anchors are computed in the target agent's timezone (doc 09), so a daily-looking interval keeps its wall time across DST.
- **Restart.** Triggers and fires are durable rows; no in-memory timers exist. A crash between enqueue and fire record is safe because the unique key on `ryusei_fire` and the inbox key both dedupe.

## Testing
No models or displays needed, except the headless client tests.
- **Pure scheduler.** `NextFire(spec, now, tz)` table tests: timer in the past, interval coalescing after downtime, DST in the agent's zone, floor enforcement.
- **Predicate evaluator.** Each closed predicate with matching and non-matching events; unknown predicates refused at creation.
- **Guards.** Chain depth at 3 and 4; cooldown; single flight under two concurrent ticks; `refused_cap` at 96 fires; auto-pause after 3 refusals.
- **Authority.** Self, coworker (allowed, ask_first, off), other project (refused), resting target (`skipped_resting`), owner override, target pausing its own trigger.
- **Pings.** Attach creates one system line and one inbox item per target keyed by `(gc, seq)`; a retried fan-out does not duplicate; fan-out above 8 refused at creation.
- **Taint.** An external-content payload starts a reader session; an agent-written purpose does not.
- **Durability.** A crash after enqueue and before the fire record leads to one wake after restart.
- **Views.** `roadmap_progress` over a fixture of objectives and linked tasks; a dependency cycle in front matter is reported, not hidden.
- **Headless.** Micro schedule pane lists triggers and fires with outcome badges; the macro board shows its trigger chips on cards; the title reads "RyuseiFuda · Board".

## Work packages
Each package has its own tests. Packages with no dependency can run in parallel (one worktree and branch `agent/<wp-id>` each).

| WP | Scope | Depends on |
|---|---|---|
| WP-R1 | Layer 1 label and pane title from the descriptor (no behaviour change) | none |
| WP-R2 | Pure trigger model: spec types, `NextFire`, predicate set, guard arithmetic | none |
| WP-R3 | Migration `ryusei/0001_triggers.sql` (two tables, append-only fire log) and its tests | Wave 0 (doc 04) |
| WP-R4 | Trigger repository and service: create, pause, resume, cancel, authority checks, caps | WP-R2, WP-R3 |
| WP-R5 | Scheduler tick: claim due rows with `SKIP LOCKED`, fire rows, inbox enqueue with doc 06 keys | WP-R4; doc 06 inbox |
| WP-R6 | Event bridge: subscribe to kanban events (doc 12), PR events (doc 21), GC messages, Vault changes, council decisions via NOTIFY | WP-R4; docs 12, 18, 21 |
| WP-R7 | GC ping and attach: system line, `new_gc` creation, fan-out cap | WP-R5; doc 01 |
| WP-R8 | Guards and loop trips: chain depth, cooldown, single flight, shared wake cap, auto-pause, Psych Ward hook | WP-R5; doc 06, doc 26 |
| WP-R9 | Agent tool contract: `ryusei.schedule`, `ryusei.list`, `ryusei.pause`, `ryusei.cancel` through dispatch and audit | WP-R4; doc 15 |
| WP-R10 | Observability: trigger and fire events for Observatory; audit entries | WP-R5; doc 23 |
| WP-R11 | Client: micro schedule pane (Avalonia.Headless test); macro board title; trigger chips on cards | WP-R4, WP-R1; docs 27, 28 |
| WP-R12 | Layer 3: Vault front-matter schema, `roadmap_*` views, Constellation node kinds, roadmap pane | doc 18, doc 19, WP-R1 |
| WP-R13 | Doc updates in one pass: doc 01 (no change), doc 06 (inbox key shape, wake kinds), doc 12 (layer pointer), doc 19 (node kinds), doc 23 (events), doc 24 (interrupt consumers), doc 26 (loop trip path) | WP-R3 done |

Waves: R1, R2, R12 (view design only) and R13 can start first. WP-R3 comes next, then R4 and R5 together with the guards. R6 to R11 follow.

## Conflicts
1. **Doc 06 inbox key. Resolved with a safe default (coordinator, 2026-11-08).** Doc 06's unique key assumes a GC sequence on every wake. The wake source can now be either a GC line or a RyuseiFuda ticket (a `ryusei_fire` row). The inbox key becomes `(agent_id, source_key, kind)`, where `source_key` is the GC line's `gc_seq` or the ticket's `fire_id`. A GC-line wake keeps the same dedupe as before. Doc 06 is updated to match in this PR. Owner may still choose a different key shape; if so, change this line and WP-R5 only.
2. **Triggers and the board.** Triggers could be read as a second way to move cards. They are not. Only `kanban.transition` moves cards, through the task service (doc 12).
3. **Doc 12 "talk-first" rule.** Auto-Ready and reopen still write a GC line. Triggers follow the same rule: every fire that speaks to others writes a line.
4. **Doc 09 curfew versus timers.** A timer at 02:00 for an agent in curfew waits for shift start. The owner may expect it to run at 02:00. Default follows doc 09; `ryusei.bypass_curfew` exists for the owner to override.
5. **Doc 19 node kinds.** Constellation currently lists task, GC, Vault and PR nodes. Objectives and phases are new kinds; doc 19 must add them (WP-R13).
6. **"Event" versus "watcher".** The owner's list has four trigger types. Here `event` is a one-shot subscription and `watcher` is a repeating condition. Both read the same predicate set, so they share code.

## Questions for the owner
None block the work. Each has a recorded default. Reply only if one is wrong.
1. **Name spelling.** Default: "RyuseiFuda" in UI and docs; the other spellings are aliases.
2. **Coworker triggers.** Default: an agent may schedule a coworker in the same project, with a GC line. Setting: `ryusei.coworker_triggers`.
3. **Curfew.** Default: triggers wait for the target's shift (doc 09). Setting: `ryusei.bypass_curfew`.
4. **Cron.** Default: not in v1. Interval and timer cover the common cases.
5. **Start dates on the board.** Default: no new column in v1; the team view uses `due_at`.
