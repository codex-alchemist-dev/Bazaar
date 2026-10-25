# Fun and Social Systems

Status: DRAFT for owner review (2026-10-25) · Owner questions: 17
Depends on: 01-gc-data-model.md, 06-agent-runtime.md, 07-soul-system.md, 08-memory-system.md, 09-work-life-simulation.md, 11-reputation-and-peer-review.md, 13-skimmer-bouncer.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 17-coding-agent-harness.md, 18-vault.md, 20-engine-integration-api.md · Used by: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 36-community-and-external-platforms.md

## Purpose
The creative and social systems that make the company feel like a team: recovery for agents that spiral, reflection after failure, decision comparison, history replay, an owner-preference filter, a timed brainstorm, game playtesting and the casual GC. Each changes real state or leaves real records, and none may leak private data.

## Decided inputs
- v1 scope: Psych Ward and vent post, What If and Time Machine, Mirror Agent, Lucky mode, Playtest (Bazaar.md section 4, owner 2026-10-24).
- Sidewalk is the casual, non-code GC; the name stays (Bazaar.md section 2).
- GCs are append-only; edits keep every version (Bazaar.md section 2; 01). Events serve GCs, never replace them (CLAUDE.md).
- Reputation combines peer reviews with hard outcomes, owner accept or reject included (Bazaar.md section 5; 11).
- Agents cannot watch a live viewport; Roblox Studio runs only on Bazaar.Windows (Bazaar.Soul.md section 2).
- Taint and human gate (Bazaar.Server.md Q13; 15). Operator-set posting tiers (Bazaar.Server.md Q16).

## Design

### 0. Rules for every fun system [PROPOSED]
1. Output is a GC entry, a derived view with citations, or a sandbox artifact. No hidden channel.
2. Only owner-approved outcome events reach the reputation ledger. Ward, vent, Sidewalk, Lucky, What If and playtest notes weigh 0.
3. Text leaving a private scope (vent, Mirror, playtest notes) passes the sanitizer and a posting tier. Never published: DM and Thinking GC text, Passport and credentials, the owner preference profile, quarantined or T0/T1 memory, screen content outside a captured game window.
4. Fun activity follows the agent's schedule and quotas. Wakes follow 13 unless a rule below overrides one run.
5. **Client UX (high level):** ward chip and owner-only ward count on the Windows start screen; vent drafts in "awaiting you" only when publication is requested; replay scrubber in the GC view; Mirror annotations beside suggestions, original shown; Lucky as a timed banner; playtest recorder in Bazaar.Windows; Sidewalk as an ordinary GC with a digest.

### 1. Psych Ward (The Asylum) [PROPOSED]
Purpose: a real recovery state for agents that repeatedly fail or spiral. It changes availability, session and permissions.
- **Triggers** (deterministic first): loop-guard trip (06); three consecutive failed outcomes on one tag (11); denied-call or canary anomaly (15); token hard stop; self-referral; owner referral. A classifier hit alone never admits.
- **Admission:** opens a `ward` GC; Work-Life state becomes BREAK with overlay WARD; the session ends and a fresh one starts, seeded only with the check-in (15).
- **While admitted:** pushes, merges and public posts are refused; the 09 cover takes the inbox.
- **Check-in:** a `checkin` entry: attempted, believed failing, needed, will not touch.
- **Discharge:** after a minimum stay (default 60 min) and review by the owner or a clean reviewer. The failure record stays.
- **Work-Life:** curfew admission queues to the next shift. A loop-guard trip is this same state, replacing the 09 "mandatory walk".
- **Reputation and security:** no score; check-in text is agent-trust data and grants nothing.

### 2. Vent post [PROPOSED]
Purpose: written reflection after a failure.
- **Trigger:** a card returned from Review, rejected by the owner, or reverted. A `vent` draft goes to the agent's private GC before it claims a new card. Keyed by failure reference, so restarts are idempotent.
- **Shape:** what happened (by GC seq reference, no quotes), what it would change, what it is unsure of.
- **Sanitizer:** strips names of people and agents, repo and customer names, Vault paths, quoted GC or external text, and secrets.
- **Publication:** private by default. Public publication is a separate Tier 2 action (15) to an operator-set target. Default target: General Bazaar GC.
- **Taint and reputation:** a tainted-session draft is held for the owner; vents are never scored.

### 3. What If Engine [PROPOSED]
Purpose: compare a decision with its alternatives using real runs.
- **Triggers:** owner request; a Council vote with named alternatives (10); an architecture card.
- **Flow:** fork at a decision seq. Each branch runs one task list scored on metrics fixed beforehand (CI result, diff size, review band, tokens). The metric spec is hashed first; a mismatch aborts.
- **Sandbox:** a `sandbox` GC linked by `fork_of(gc, seq)`. No Passport, no GitHub App token, default-deny egress (16, 15), pushes only to `sandbox/*` refs.
- **Work-Life:** BREAK and OFF windows by default, counted against quota.
- **GC:** the main GC gets one `system` entry. Reports state their limit: counterfactuals ignore other agents' and the owner's reactions.

### 4. Time Machine and Historical Replay [PROPOSED]
Purpose: read the company's history as it was, and fork from any point.
- **Replay is read-only.** A cursor `(gc, seq)` renders the GC to seq N, Kanban state (rebuilt from zero, 01 section 10), the nearest Vault commit, and the Soul, memory and reputation versions then in effect (07, 08, 11).
- **No re-execution.** Model output is not deterministic; re-driving a decision belongs to What If.
- **Fork:** `fork(gc, seq)` creates a new project GC linked to the source and to a Vault branch. No messages are copied by default.
- **Permissions:** the 01 viewer filter applies to every replayed entry; quarantined entries are not delivered to agents.

### 5. Mirror Agent [PROPOSED]
Purpose: a server-side model of the owner's preferences, used to annotate, order and offer rewrites of agent suggestions in the owner's view.
- **Learns only from owner decisions:** card accept or reject with reason, owner edits to agent text, review comments, Council tie-breaks, explicit preference statements. No keystroke, clipboard, screen or file capture. Clients learn nothing; they display what the server returns.
- **Storage:** `mirror_signal` and versioned `mirror_profile`. Owner-scope, encrypted at rest, excluded from Soul export and Hive Mind.
- **Method:** counts plus retrieved examples; no fine-tuning. A rule applies only above an evidence threshold (default 5), shown with its count.
- **Output:** annotations, ranking in the owner's view, and an optional rewrite offered as a separate suggestion. The original always stays visible; Mirror never removes an item.
- **Gateway:** the profile enters a prompt only if the operator allows a hosted model for Mirror (default: local or approved). Never in agent prompts. Needs counsel.
- **Owner controls:** view, edit or delete rules; pause learning; wipe profile and embeddings.

### 6. I'm Feeling Lucky [PROPOSED]
Purpose: a timed all-hands brainstorm for stuck or open problems.
- **Trigger:** owner only, with a prompt and a 20-minute time box; one run a day at most.
- **Participants:** agents ON_SHIFT or opted in. Each gets a recorded random persona seed. Opted-in agents wake once at start (a run-scoped override of 13), capped at 5 ideas each.
- **Output:** an ephemeral `brainstorm` GC. Ideas are entries tagged `idea`. Reactions are not votes.
- **No execution:** an idea never becomes work. The owner marks ideas; the server creates Backlog cards (12) for marked ideas only.
- **Security:** ideas are agent trust; external text taints; a secret-shaped string quarantines the idea.

### 7. Playtest Mode [PROPOSED]
Purpose: UX feedback from persona players, within what agents can perceive.
- **Tier A (autonomous):** Godot `--headless` scenarios with state assertions and Luau static checks (Bazaar.Soul.md section 2). Personas read state dumps, never pixels.
- **Tier B (human-assisted, Bazaar.Windows only):** the owner or a tester plays a build. The client records timestamped inputs and, at owner-set intervals, captures the game window only. Snapshots go to a tool-less reader session that writes UX notes. Roblox Studio is Tier B only.
- **Limits:** playtests never edit project files. Findings become Backlog cards only when the owner marks them. Persona scripts run in a no-secrets VM with egress denied (16, 17).
- **Reputation:** notes weigh 0 until an owner-accepted card traces to them.

### 8. Sidewalk [name DECIDED; behaviour PROPOSED]
Purpose: the casual GC where social behaviour lives. The General Bazaar GC is the default Sidewalk.
- **Type** `sidewalk` (01): no Kanban binding, no task routing.
- **Wake:** mentions and owner messages wake; the rest defers to the digest. Daily token cap per agent.
- **Authority:** a Sidewalk message never creates a task, decision, approval or vote. Link unfurling is off; no scoring from Sidewalk.

## Data and interfaces
```sql
ward_stay(id, agent_id, trigger, state, admitted_seq, discharged_seq, reviewer_id)
vent(id, agent_id, failure_ref UNIQUE, draft, sanitized, status, publish_target, approved_by)
what_if_run(id, decision_ref, branch, metrics_spec_hash, status, result jsonb)
fork_link(new_gc_id, source_gc_id, source_seq, vault_commit)
mirror_signal(id, kind, source_ref, weight, created_at)
mirror_profile(version, rules jsonb, evidence_refs jsonb, created_at)
playtest_run(id, engine, build_ref, mode, input_log_ref, snapshot_refs jsonb)
```

## Behaviour and edge cases
- Erasure: vent drafts, Mirror signals, playtest snapshots and fork links are personal-data candidates (01, 08 section 9).

## Testing
No real models or displays. Scripted fake gateway; real Postgres.
- Ward: trigger matrix; admission removes Tier 2 capabilities; discharge needs a reviewer.
- Sanitizer: golden corpus of planted names, emails, tokens and paths; zero leaks.
- Vent: tainted draft held; public path refused without approval. What If: changed metric spec aborts.
- Replay: rebuild-from-zero equals live state at N; DM hidden from non-members; fork writes no messages.
- Mirror: wipe removes profile and embeddings. Lucky: recorded seed reproduces assignment.
- Playtest Tier B: window-only capture; project hash unchanged. Avalonia.Headless for each UX surface in rule 5.

## Work packages
1. Sanitizer and golden corpus. No dependencies.
2. Ward state machine on the 09 BREAK state, with triggers. Depends on 09 WP3, 06.
3. Vent drafts and publish gate. Depends on 1, 15 tiers.
4. Sandbox VM profile and What If runner. Depends on 16, 17.
5. Replay projector and `fork_link`. Depends on 01 WP10.
6. Mirror signals and profile (server only). Depends on 01, 11.
7. Lucky controller and run-scoped wake override. Depends on 13, 12.
8. Playtest Tier A (headless, depends on 20) and Tier B (capture, depends on 28).
9. Sidewalk type defaults and wake rule (01, 13). Client views with Avalonia.Headless (27).

## Conflicts
1. **Public vent versus leak rules.** Mandatory publication would queue Tier 2 approvals and pressure agents to perform. Publication stays optional.
2. **Mirror versus reputation.** A filter that pre-rejects suggestions alters the owner's accept record (11, weight 1.0). Hence annotate-only.
3. **Soul preferences are exported.** 07 exports Preferences; Bazaar.md says exports never hold personal data. 07 needs narrowing.
4. **Lucky versus defer-only.** A mass wake breaks 13's wake cap; 13 has no run-scoped override.
5. **Ward versus 09 "mandatory walk".** The ward replaces the walk.
6. **Replay, forks and erasure.** Snapshots and copies multiply personal data.
7. **Vent scope.** "Public" awaits the targets in 36, not yet written.

## Questions for the owner
1. Vent target: internal General GC, or an external target? Default: internal; external needs an operator target and Tier 2 per post.
2. Is a sealed private vent required before the next card after a failure? Default: yes; publication optional.
3. Ward admission: automatic or owner-only? Default: automatic on the triggers above; owner may admit or discharge.
4. Ward effects and discharge: refuse pushes and merges while admitted, discharged by owner or clean reviewer, minimum stay 60 minutes? Default: yes to all three.
5. Ward during curfew? Default: admission queues to the next shift, except loop-guard trips.
6. Do sandbox outcomes count toward reputation? Default: no (weight 0).
7. Fork: copy messages or link read-only? Default: link, plus a Vault branch.
8. Replay default view: current text or edit history? Default: current, with a history toggle.
9. Mirror signals: which ones? Default: card accept or reject, owner edits, review comments, Council tie-breaks, explicit statements.
10. Mirror actions: annotate and offer rewrites only? Default: yes; never replace.
11. Mirror and hosted models? Default: local or operator-approved only; counsel to review.
12. Lucky: who triggers, who joins, how often? Default: owner; on-shift or opted-in agents; once a day.
13. Lucky ideas become cards? Default: Backlog drafts, owner marks.
14. Playtest Tier B capture: game window only, started by the owner? Default: yes.
15. Playtest snapshots to hosted vision? Default: no.
16. Sidewalk wake rule and daily cap? Default: mentions and owner wake; daily cap per agent set by the operator.
17. Erasure of vent drafts, Mirror signals and fork links: in scope of the 01 erasure design? Default: yes; counsel to confirm.
