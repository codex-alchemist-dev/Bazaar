# Fun and Social Systems

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 19
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 07-soul-system.md, 08-memory-system.md, 09-work-life-simulation.md, 11-reputation-and-peer-review.md, 12-kanban-and-dependencies.md, 13-skimmer-bouncer.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 17-coding-agent-harness.md, 18-vault.md, 20-engine-integration-api.md, 33-backup-dr-and-export.md, 36-community-and-external-platforms.md · Used by: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 34-testing-strategy.md, 36-community-and-external-platforms.md

## Purpose
The creative and social systems that make the company feel like a team: recovery for agents that spiral, reflection after failure, decision comparison, history replay, an owner-preference filter, a timed brainstorm, game playtesting and the casual GC. Each changes real state or leaves real records, and none may leak private data.

## Decided inputs
- v1 scope: Psych Ward and vent post, What If and Time Machine, Mirror Agent, Lucky mode, Playtest (Bazaar.md section 4, owner 2026-10-24).
- Sidewalk is the casual, non-code GC; the name stays (Bazaar.md section 2).
- GCs are append-only; edits keep every version in `gc_message_version` (01, 04; round 1, C09). GC types are rows in a registry (round 6, C19). Events serve GCs, never replace them (CLAUDE.md).
- Personal fields are per-person encrypted payloads; erasure destroys the key and appends a tombstone (round 2, C01). Legal sufficiency needs counsel.
- External content taints a session hard; agent text taints softly, only from a tainted source session; Tier 2 actions gate on hard taint (round 2, C06). Reader sessions read untrusted input and hand over summaries (round 6, C18; Claude's reading, owner to confirm).
- Approval tiers follow 15's ladder with 36's stricter community defaults (round 7, C21).
- Wake reasons are owned by 06; the Skimmer may promote a digest item early under a cap (round 3, C08). Any loop-guard trip admits the agents involved to the Psych Ward, with no separate break (round 9, C34).
- Thinking GCs nest under each working GC; never injected into another agent's context; vote reasoning hidden until the reveal (round 3, C11).
- Memory and other written artifacts live in the Vault; Postgres holds the GC log, structured state and indexes (round 11, C38).
- Git pushes run host-side; tokens never enter a VM (round 2, C05). Sandbox and persona roles are profiles on the two per-agent VM images (round 7, C22).
- Presence uses eight labels plus overlays (round 11, C43). Roblox Studio runs only on Bazaar.Windows (Bazaar.Soul.md section 2).

## Design

### 0. Rules for every fun system [PROPOSED]
1. Output is a GC entry, a derived view with citations, or a sandbox artifact. No hidden channel.
2. Only owner-approved outcome events reach the reputation ledger. Ward, vent, Sidewalk, Lucky, What If and playtest notes weigh 0.
3. Text leaving a private scope (vent, Mirror, playtest notes) passes the sanitizer and a posting tier. Never published: DM and Thinking GC text, Passport and credentials, the owner preference profile, quarantined or T0/T1 memory (08 tiers), screen content outside a captured game window.
4. Fun activity follows the agent's schedule and quotas (09). Wakes follow 06 and 13, unless a rule below names a wake reason.
5. **Client UX (high level):** ward chip and owner-only ward count on the Windows start screen; vent drafts in "awaiting you" only when publication is requested; replay scrubber in the GC view; Mirror annotations beside suggestions, original shown; Lucky as a timed banner; playtest recorder in Bazaar.Windows; Sidewalk as an ordinary GC with a digest.

### 1. Psych Ward (The Asylum) [PROPOSED]
Purpose: a real recovery state for agents that repeatedly fail or spiral. It changes availability, session and permissions.
- **Triggers** (deterministic first): loop-guard trip (06); three consecutive failed outcomes on one tag (11); denied-call or canary anomaly (15); token hard stop (15 shared limits); self-referral; owner referral. A classifier hit alone never admits.
- **Admission:** opens a `ward` GC (registered type); Work-Life state becomes BREAK with overlay WARD (09); the session ends and a fresh one starts, seeded only with the check-in (15).
- **While admitted:** pushes, merges and public posts are refused; the 09 cover takes the inbox.
- **Check-in:** a `checkin` entry: attempted, believed failing, needed, will not touch.
- **Discharge:** after a minimum stay (default 60 min) and review by the owner or a clean reviewer. The failure record stays.
- **Work-Life:** curfew admission queues to the next shift (Q5). A loop-guard trip replaces the 09 "mandatory walk" and the 15-minute break; 09 and 06 drop their own definitions (C34).
- **Reputation and security:** no score. Check-in text is agent trust data (source_trust `agent`, 15) and grants nothing.

### 2. Vent post [PROPOSED]
Purpose: written reflection after a failure.
- **Trigger:** a card returned from Review, rejected by the owner, or reverted (12). A draft goes to the agent's private `vent` GC (type registered in 01) before it claims a new card. Keyed by failure reference, so restarts are idempotent.
- **Shape:** what happened (by GC seq reference, no quotes), what it would change, what it is unsure of.
- **Sanitizer:** strips names of people and agents, repo and customer names, Vault paths, quoted GC or external text, and secrets.
- **Publication:** private by default and optional. Public publication is a separate Tier 2 action (15) to an operator-set target from 36. Default target: the General Bazaar GC.
- **Taint and reputation:** a draft from a hard-tainted session is held for the owner. A draft from a softly tainted session is held only if its source session was tainted (C06). Vents are never scored.

### 3. What If Engine [PROPOSED]
Purpose: compare a decision with its alternatives using real runs.
- **Triggers:** owner request; a Council vote with named alternatives (10); an architecture card.
- **Flow:** fork at a decision seq. Each branch runs one task list scored on metrics fixed beforehand (CI result, diff size, review band, tokens). The metric spec is hashed first; a mismatch aborts.
- **Sandbox:** a `sandbox` GC linked by a `fork_link` row. No Passport and no GitHub App token; default-deny egress (16, 15). Pushes are host-side (C05) and land only on `sandbox/*` refs.
- **Work-Life:** BREAK and OFF windows by default, counted against quota.
- **GC:** the main GC gets one `system` entry. Reports state their limit: counterfactuals ignore other agents' and the owner's reactions.

### 4. Time Machine and Historical Replay [PROPOSED]
Purpose: read the company's history as it was, and fork from any point.
- **Replay is read-only.** A cursor `(gc, seq)` renders the GC to seq N: each message at its latest version at or below N (`gc_message_version`); Kanban state rebuilt from the task event log (12); the nearest Vault commit (18), which holds the memory then in effect; and the Soul and reputation versions then in effect (07, 11).
- **No re-execution.** Model output is not deterministic; re-driving a decision belongs to What If.
- **Fork:** `fork(gc, seq)` creates a new project GC linked to the source and to a Vault branch. No messages are copied by default.
- **Permissions:** the 01 viewer filter applies to every replayed entry; quarantined entries are not delivered to agents.

### 5. Mirror Agent [PROPOSED]
Purpose: a server-side model of the owner's preferences, used to annotate, order and offer rewrites of agent suggestions in the owner's view.
- **Learns only from owner decisions:** card accept or reject with reason, owner edits to agent text, review comments, Council tie-breaks, explicit preference statements. No keystroke, clipboard, screen or file capture. Clients learn nothing; they display what the server returns.
- **Storage:** `mirror_signal` and versioned `mirror_profile` in Postgres (Q18), with encrypted payloads (C01). Owner-scope; excluded from the Soul export (07) and from Hive Mind (25).
- **Method:** counts plus retrieved examples; no fine-tuning. A rule applies only above an evidence threshold (default 5), shown with its count.
- **Output:** annotations, ranking in the owner's view, and an optional rewrite offered as a separate suggestion. The original always stays visible; Mirror never removes an item (this keeps the owner's accept record in 11 unfiltered).
- **Gateway:** the profile enters a prompt only if the operator allows a hosted model for Mirror (default: local or approved). Never in agent prompts. Needs counsel.
- **Owner controls:** view, edit or delete rules; pause learning; wipe profile and embeddings.

### 6. I'm Feeling Lucky [PROPOSED]
Purpose: a timed all-hands brainstorm for stuck or open problems.
- **Trigger:** owner only, with a prompt and a 20-minute time box; one run a day at most.
- **Participants:** agents ON_SHIFT or opted in. Each gets a recorded random persona seed. Opted-in agents are woken once at start by a 06 wake reason (proposed name `brainstorm`), not by a 13 override (C08), and each is capped at 5 ideas.
- **Output:** an ephemeral `brainstorm` GC. Ideas are entries tagged `idea`, with source_trust `agent`. Reactions are not votes.
- **No execution:** an idea never becomes work. The owner marks ideas; the server creates Backlog cards (12) for marked ideas only.
- **Security:** external text taints the session (C06); a secret-shaped string quarantines the idea.

### 7. Playtest Mode [PROPOSED]
Purpose: UX feedback from persona players, within what agents can perceive.
- **Tier A (autonomous):** Godot `--headless` scenarios with state assertions and Luau static checks (Bazaar.Soul.md section 2). Personas read state dumps, never pixels.
- **Tier B (human-assisted, Bazaar.Windows only):** the owner or a tester plays a build. The client records timestamped inputs and, at owner-set intervals, captures the game window only. Snapshots upload through the authenticated blob surface in 02 (C15). A tool-less reader session writes UX notes. Roblox Studio is Tier B only.
- **Limits:** playtests never edit project files. Findings become Backlog cards only when the owner marks them. Persona scripts run in a no-secrets VM profile with egress denied (16, 17).
- **Reputation:** notes weigh 0 until an owner-accepted card traces to them.

### 8. Sidewalk [name DECIDED; behaviour PROPOSED]
Purpose: the casual GC where social behaviour lives. The General Bazaar GC is the default Sidewalk.
- **Type** `sidewalk` (registered in 01): no Kanban binding, no task routing.
- **Wake:** mentions and owner messages wake. Other messages reach the digest; the Skimmer may promote one early under the 13 cap (C08). Daily token cap per agent (Q17).
- **Authority:** a Sidewalk message never creates a task, decision, approval or vote. Link unfurling is off; no reputation scoring from Sidewalk.

## Data and interfaces
This doc owns these tables (C16). Agent, turn and inbox tables are 06's; schedule and availability are 09's; shared quota limits are in 15's table (C36).
```sql
ward_stay(id, agent_id, trigger, state, admitted_seq, discharged_seq, reviewer_id)
vent(id, agent_id, failure_ref UNIQUE, draft_payload, sanitized_payload, status, publish_target, approved_by)
  -- payload columns are per-person encrypted (C01)
what_if_run(id, decision_ref, branch, metrics_spec_hash, status, result jsonb)
fork_link(new_gc_id, source_gc_id, source_seq, vault_commit)
mirror_signal(id, kind, source_ref, weight, payload, created_at)  -- payload encrypted
mirror_profile(version, rules jsonb, evidence_refs jsonb, created_at)  -- encrypted
playtest_run(id, engine, build_ref, mode, input_log_ref, snapshot_blob_refs jsonb)
```

## Behaviour and edge cases
- **Erasure (C01):** vent drafts, Mirror signals and profile payloads, and playtest snapshot blobs are personal-data candidates. Erasure destroys the key and appends a tombstone; structure and timestamps stay. `fork_link` rows are metadata. Backup retention equals the erasure window (33). Counsel confirms legal sufficiency.
- **Vault material:** replayed memory and Vault commits are personal-data candidates inside Markdown and git history. That answer is open (Q19 and 18).
- **Ward versus wake:** a ward admission removes Tier 2 capabilities and queues wakes; it does not drop inbox items (06).
- **Lucky cap:** one run a day; the run-scoped wake ends when the time box does.

## Testing
No real models or displays. Scripted fake gateway; real Postgres.
- Ward: trigger matrix; every loop-guard trip admits; admission removes Tier 2 capabilities; discharge needs a reviewer.
- Sanitizer: golden corpus of planted names, emails, tokens and paths; zero leaks.
- Vent: hard-tainted draft held; soft-tainted draft held only with a tainted source; public path refused without approval. Erasure tombstone makes a vent draft unreadable.
- What If: changed metric spec aborts.
- Reader session: output validated against schema; untrusted text never reaches the actor session.
- Replay: rebuild-from-zero equals live state at N; DM hidden from non-members; fork writes no messages.
- Mirror: wipe removes profile and embeddings; annotate-only leaves the original item visible.
- Lucky: recorded seed reproduces assignment; wake arrives through the 06 reason, not a 13 override.
- Playtest Tier B: window-only capture; project hash unchanged. Avalonia.Headless for each UX surface in rule 5.

## Work packages
1. Sanitizer and golden corpus. No dependencies.
2. Ward state machine on the 09 BREAK state, with triggers. Depends on 09, 06.
3. Vent drafts and publish gate. Depends on 1, 15 tiers, 14 encryption key table (04).
4. Sandbox VM profile and What If runner. Depends on 16, 17.
5. Replay projector and `fork_link`. Depends on 01, 04, 18.
6. Mirror signals and profile (server only). Depends on 01, 11.
7. Lucky controller and the `brainstorm` wake reason. Depends on 06, 12, 13 (promotion cap only).
8. Playtest Tier A (headless, depends on 20) and Tier B (capture, depends on 02 blob surface and 28).
9. Sidewalk type defaults and wake rule (01, 13). Client views with Avalonia.Headless (27).

## Conflicts
Remaining only. Cross-doc fixes are listed for the owning doc.
1. **Vent target (36).** 36 must name the operator-set posting targets that publication refers to (Q1).
2. **Soul preferences are exported (07).** 07 exports Preferences; Bazaar.md says exports never hold personal data, and Mirror is excluded. 07 must narrow the export.
3. **Mandatory walk (09, 06).** 09 still defines a 15-minute BREAK after a loop-guard trip, and 06 a digest-only pause. Both must go in favour of the ward (C34).
4. **Run-scoped wake (13, 06).** 13 has no run-scoped override. 06 must add the `brainstorm` wake reason; 13 may only promote early under its cap (C08, Q14).
5. **Reader-session primitive (15).** Playtest notes, Mirror, Lucky and Sidewalk rely on a tool-less reader session. 15 must define it and its hand-over format (C18).
6. **GC type rows (01).** 01 must register `ward`, `vent`, `sandbox`, `brainstorm` and `sidewalk` (C19).
7. **Presence labels (09).** 09 must adopt the eight labels and overlays (C43); WARD is an overlay.
8. **Erasure in Vault history (18, 33).** Memory and commits holding personal data cannot be erased by key destruction. Open (C38 consequence).
9. **Client capture (17, 28).** Playtest window capture is not in the ratified client-side exceptions (C17). See Q15.

## Questions for the owner
1. Vent target: internal General GC, or an external target? Default: internal; external needs an operator target from 36 and a Tier 2 approval per post.
2. Is a sealed private vent required before the next card after a failure? Default: yes; publication optional.
3. Ward admission: automatic or owner-only? Default: automatic on the triggers above; the owner may admit or discharge.
4. Ward effects and discharge: refuse pushes and merges while admitted; discharge by the owner or a clean reviewer; minimum stay 60 minutes? Default: yes to all three.
5. Ward during curfew? Default: admission queues to the next shift.
6. Do sandbox outcomes count toward reputation? Default: no (weight 0).
7. Fork: copy messages or link read-only? Default: link, plus a Vault branch.
8. Replay default view: current text or edit history? Default: current, with a history toggle.
9. Mirror signals: which ones? Default: card accept or reject, owner edits, review comments, Council tie-breaks, explicit statements.
10. Mirror actions: annotate and offer rewrites only? Default: yes; never replace.
11. Mirror and hosted models? Default: local or operator-approved only; counsel to review.
12. Lucky: who triggers, who joins, how often? Default: owner; on-shift or opted-in agents; once a day.
13. Lucky ideas become cards? Default: Backlog drafts that the owner marks.
14. Lucky wake: a 06 wake reason (`brainstorm`), once per opted-in agent per run, instead of any 13 override? Default: yes.
15. Playtest Tier B capture (window only, started by the owner) is not in the C17 list of client-side exceptions. Ratify it as a Bazaar.Windows exception, Tier B only? Default: yes.
16. Playtest snapshots to hosted vision? Default: no.
17. Sidewalk daily token cap: value, and whether it draws on the shared table in 15 (C36)? Default: operator-set, with the 15 default.
18. Mirror profile storage: encrypted Postgres (default) or Vault Markdown, as the C38 principle would suggest? Default: Postgres, because a git-history profile is hard to erase and it is a model, not a written artifact. Counsel to confirm.
19. Vent drafts: private GC entries (default) or Vault notes under the C38 principle? Default: GC entries, encrypted per person.

ANSWERED: erasure is per-person encrypted payloads with key destruction and tombstones from day 1; legal sufficiency still needs counsel (DECISIONS-LOG round 2, C01).
ANSWERED: Sidewalk wake rule follows the general rule: mentions and owner messages wake; Skimmer promotion is capped (DECISIONS-LOG round 3, C08).
ANSWERED: loop-guard trip goes to the Psych Ward on any trip, with no separate 15-minute break (DECISIONS-LOG round 9, C34).
