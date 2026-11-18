# Nervous System (internal event bus)

Status: PROPOSED (2026-11-18). The owner has said one thing about this feature (section 1). Everything else here either restates a decision already made in another doc or is marked as a recommendation for the owner. Nothing in this doc is built as a unit, and no module is created until the open questions in section 6 are answered.
Depends on: `04-postgres-schema-and-migrations.md` (NOTIFY, outbox conventions), `06-agent-runtime.md` (inbox, wake reasons), `12-kanban-and-dependencies.md` (task events), `21-github-integration.md` (PR events), `38-ryusei-fuda.md` (event bridge) · Used by: `23-observatory-and-usage.md`, `24-company-systems-core.md`, `46-kodo-health-module.md`

## Purpose
Gives the name "Nervous System" a single written meaning: the internal events that let Bazaar's software react (move a card, notify, write a history line) without an agent having to be asked. It exists to make the existing event sources one documented family. It is not a new broker and not a way for agents to coordinate.

## 1. What the owner decided
Source: `docs/deep-dives/Bazaar.md` section 2, tagged PROPOSED.

- There is an internal event bus ("The Nervous System").
- Example events: `PRMerged`, `AgentMentioned`, `SceneChanged`, "etc." The list is open.
- Events trigger reactions. The examples given are Kanban updates, notifications and a history log.
- It must not replace GCs. Agents talk through GCs. Events are how the software reacts, not how agents think together.

That is all. `FEATURES.md` lists it as Planned with no other source. The rest of this doc is not spec the owner wrote.

## 2. Rules already in force that bind it
These are DECIDED elsewhere and apply to any event design.

1. **GCs are the coordination system.** Events and software reactions serve GCs and never replace them (`CLAUDE.md`; docs 06, 12, 38). Anything an agent or person should read is a GC line, written by the service that owns the state, never by the event itself (doc 12: "Events never write GC messages; the task service does").
2. **Fan-out is Postgres NOTIFY.** GC fan-out uses `NOTIFY`. Valkey or Garnet serve cache and presence only (DECISIONS-LOG round 1, C07). An event design must not require a Valkey or Garnet pub/sub.
3. **NOTIFY is a prompt, not the record.** It is delivered on commit only, and a disconnected listener misses it. Consumers re-read the durable row above their last seen position after a reconnect (doc 04, doc 02).
4. **Announced once, after commit.** Events are never announced for a rolled-back write (doc 12 "Notify").
5. **Triggers carry no authority.** An event can wake an agent. The agent then acts under its own permissions, taint state and approvals (docs 15, 38).
6. **Wake reasons belong to doc 06.** A consumer enqueues an `inbox_item` with an idempotent key. It does not invent wake kinds (doc 06, doc 21 event mapping).
7. **Loop guards apply.** Event-driven wake chains are bounded by doc 06 and doc 38 guards. A trip goes to the Psych Ward (docs 06, 26).
8. **The owner's modularity rule.** A feature is its own module project plus its `.Abstractions`, and Server and Client.Core gain no feature code (`CLAUDE.md`).

## 3. Event sources that already exist
The family already has members. This table is a census of what other docs define, not new design.

| Source | Where defined | Durable record | Event kinds (as named there) | Status |
|---|---|---|---|---|
| GC log | docs 01, 04 | `gc_event` with per-GC `seq` | `gc.message`, system lines, `pr_event` entries | DECIDED (round 1, C07, C09) |
| Task service | doc 12 | `task_events` outbox, NOTIFY after commit | `task.created`, `task.transitioned`, `task.blocked`, `task.unblocked`, `task.edge_changed`, `task.guard_refused`, `task.gate_decided`, `task.due_passed` | PROPOSED (doc 12); mapped by RyuseiFuda |
| Council | doc 10 | GC events | `council.open`, `council.decision_recorded` | PROPOSED |
| GitHub | doc 21 | `github_delivery`, `pr_event` GC entry | review requested, review changes requested, @-mention, failed check, push, closed or merged | PROPOSED |
| Agent runtime | doc 06 | `turn`, `turn_step`, `guard_event` | turn and tool lifecycle, wake reasons | DECIDED shape (doc 06) |
| Availability and quota | docs 09, 05 | their own tables | availability transitions, quota changes | PROPOSED |
| RyuseiFuda event bridge | doc 38 WP-R6 | trigger and fire tables | consumes task, GC and council events; Vault and PR sources not wired (DECISIONS-LOG RyuseiFuda table) | Built in part |
| Observatory projector | doc 23 | activity rows with monotonic `revision` | consumes the sources above | PROPOSED |

**Owner's example names mapped to what exists.** `PRMerged` is doc 21's merged `pr_event` that doc 12 consumes to move a PR card to Done. `AgentMentioned` is doc 06's `mention` wake reason. `SceneChanged` maps to nothing in any doc (see question 3).

## 4. What this doc adds (recommendation, PROPOSED)
Kept small on purpose.

1. **One meaning.** "Nervous System" means the set of durable, post-commit internal events above, each owned by the service that writes its state, consumed by reactions (task moves, notifications, history, projections, RyuseiFuda triggers). There is no central broker and no second source of truth.
2. **One naming rule.** Event kinds are lowercase dotted names (`task.transitioned`, `gc.message`) as the existing docs already use. The owner's examples (`PRMerged`) are read as descriptions, not identifiers.
3. **A shared envelope, contracts only.** If a module needs to subscribe across sources, it uses one small contract (kind, source, subject id, durable position, occurred-at) published in a `.Abstractions` assembly. Each source keeps its own table. This is the only new code surface proposed, and only if the owner chooses option A in question 1.

## 5. What it must never do
- Carry agent-to-agent coordination or replace a GC line (`Bazaar.md` section 2).
- Be the only record of anything. Every event has a durable row in the owning service.
- Reach clients as a second channel. Clients get state through the wire streams in doc 02.
- Move money or grant authority (Resource Economy is REJECTED).

## 6. Open questions for the owner
1. **Does the Nervous System become a module, or stay a documented convention over the existing sources?** Options: A) a thin contracts-only module (`Bazaar.Server.Events` plus `.Abstractions`: envelope, kind registry, cross-source subscription) with no broker; B) no new module, doc 53 stays a naming and rules doc and each source stays as is; C) a full internal bus. Recommended: A. It meets the modularity rule without a second broker, and keeps every source's own outbox as the truth. C is not recommended, because C07 already rejected a pub/sub provider dependency.
2. **Which "history log" does the owner mean?** Options: A) the existing GC log plus per-service event tables (nothing new); B) a separate append-only company event history. Recommended: A. B would duplicate the GC log and the audit log.
3. **What is `SceneChanged`?** No doc defines a scene. Options: A) an engine event from doc 20 (a Godot or Roblox scene changed, surfaced by the engine adapter); B) a Constellation view change; C) drop the example. Recommended: A, since doc 20 already names `scenes` as an adapter capability.
4. **Vault and PR event sources.** Doc 38 leaves these inert until a Vault NOTIFY and a persisted PR event table exist. Options: A) build them first as the source work this doc names; B) leave inert. Recommended: A, scoped to those two sources.
5. **Event retention.** Options: A) follow each owning table's own retention; B) one company-wide retention setting. Recommended: A, which needs no new rule.

## 7. Tests (if question 1 = A)
- The envelope round-trips through the wire codec with no loss.
- An event is announced only after commit, and not for a rolled-back write.
- A listener restart replays exactly the missed positions per source, with no gap and no duplicate reaction (idempotent inbox key).
- A reaction never writes a GC line directly; the architecture test checks the module references no GC writer.
- The module references no Valkey or Garnet client.

## 8. Work packages (if question 1 = A)
1. Contracts: envelope, kind registry, subscription interface (`Bazaar.Server.Events.Abstractions`).
2. Registry of the source kinds in section 3, as data. Needs 1.
3. Adapters over each source's existing durable position. Needs 1 and the owning docs.
4. Vault NOTIFY and persisted PR event source (question 4). Needs docs 18 and 21.
5. Docs: add the module to doc 40 in the same PR as the code (`CLAUDE.md`).

Docs section this implements: none yet. This doc is the design proposal.
