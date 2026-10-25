# Fun and Social Systems

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 5
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 03-server-host-and-providers.md, 04-postgres-schema-and-migrations.md, 06-agent-runtime.md, 07-soul-system.md, 08-memory-system.md, 09-work-life-simulation.md, 11-reputation-and-peer-review.md, 12-kanban-and-dependencies.md, 13-skimmer-bouncer.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 16-agent-vms-and-environments.md, 17-coding-agent-harness.md, 18-vault.md, 20-engine-integration-api.md, 33-backup-dr-and-export.md, 36-community-and-external-platforms.md · Used by: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md, 34-testing-strategy.md, 36-community-and-external-platforms.md

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
- Configurability rule (round 13): plain values and policy choices are settings with safe defaults; they may tighten a floor, never loosen it.

## Design

### 0. Rules for every fun system [PROPOSED]
1. Output is a GC entry, a derived view with citations, or a sandbox artifact. No hidden channel.
2. Only owner-approved outcome events reach the reputation ledger. Ward, vent, Sidewalk, Lucky, What If and playtest notes weigh 0 by default (see `what_if.reputation_weight`).
3. Text leaving a private scope (vent, Mirror, playtest notes) passes the sanitizer and a posting tier. Never published: DM and Thinking GC text, Passport and credentials, the owner preference profile, quarantined or T0/T1 memory (08 tiers), screen content outside a captured game window.
4. Fun activity follows the agent's schedule and quotas (09). Wakes follow 06 and 13, unless a rule below names a wake reason.
5. **Client UX (high level):** ward chip and owner-only ward count on the Windows start screen; vent drafts in "awaiting you" only when publication is requested; replay scrubber in the GC view; Mirror annotations beside suggestions, original shown; Lucky as a timed banner; playtest recorder in Bazaar.Windows; Sidewalk as an ordinary GC with a digest.

### 1. Psych Ward (The Asylum) [PROPOSED]
Purpose: a real recovery state for agents that repeatedly fail or spiral. It changes availability, session and permissions.
- **Triggers** (deterministic first): loop-guard trip (06); three consecutive failed outcomes on one tag (11); denied-call or canary anomaly (15); token hard stop (15 shared limits); self-referral; owner referral. A classifier hit alone never admits.
- **Admission:** opens a `ward` GC (registered type); Work-Life state becomes BREAK with overlay WARD (09); the session ends and a fresh one starts, seeded only with the check-in (15). Who may admit follows `ward.admission_mode`; the owner can always admit or discharge.
- **While admitted:** pushes, merges and public posts are refused (fixed, not a setting); the 09 cover takes the inbox.
- **Check-in:** a `checkin` entry: attempted, believed failing, needed, will not touch.
- **Discharge:** after the minimum stay (`ward.min_stay_minutes`) and review by whoever `ward.discharge_by` allows. The failure record stays.
- **Work-Life:** a curfew admission follows `ward.curfew_behaviour`. A loop-guard trip replaces the 09 "mandatory walk" and the 15-minute break; 09 and 06 drop their own definitions (C34).
- **Reputation and security:** no score. Check-in text is agent trust data (source_trust `agent`, 15) and grants nothing.

### 2. Vent post [PROPOSED]
Purpose: written reflection after a failure.
- **Trigger:** a card returned from Review, rejected by the owner, or reverted (12). When `vent.required_before_card` is on, a draft goes to the agent's private `vent` GC (type registered in 01) before it claims a new card. Keyed by failure reference, so restarts are idempotent.
- **Shape:** what happened (by GC seq reference, no quotes), what it would change, what it is unsure of.
- **Sanitizer:** strips names of people and agents, repo and customer names, Vault paths, quoted GC or external text, and secrets.
- **Publication:** private by default; publication is optional. Public publication is a separate Tier 2 action (15) to the target in `vent.publish_target`. The default target is the internal General Bazaar GC. An external target needs an operator-set target from 36 and counsel review (see the Settings table).
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
- **Replay is read-only.** A cursor `(gc, seq)` renders the GC to seq N: each message at its latest version at or below N (`gc_message_version`); Kanban state rebuilt from the task event log (12); the nearest Vault commit (18), which holds the memory then in effect; and the Soul and reputation versions then in effect (07, 11). The default view follows `replay.default_view`.
- **No re-execution.** Model output is not deterministic; re-driving a decision belongs to What If.
- **Fork:** `fork(gc, seq)` creates a new project GC linked to the source and to a Vault branch. No messages are copied by default (Q1).
- **Permissions:** the 01 viewer filter applies to every replayed entry; quarantined entries are not delivered to agents.

### 5. Mirror Agent [PROPOSED]
Purpose: a server-side model of the owner's preferences, used to annotate, order and offer rewrites of agent suggestions in the owner's view.
- **Learns only from owner decisions:** card accept or reject with reason, owner edits to agent text, review comments, Council tie-breaks, explicit preference statements (the set is `mirror.signal_kinds`). No keystroke, clipboard, screen or file capture. Clients learn nothing; they display what the server returns.
- **Storage:** `mirror_signal` and versioned `mirror_profile` in Postgres (Q4), with encrypted payloads (C01). Owner-scope; excluded from the Soul export (07) and from Hive Mind (25).
- **Method:** counts plus retrieved examples; no fine-tuning. A rule applies only above `mirror.evidence_threshold`, shown with its count.
- **Output:** annotations, ranking in the owner's view, and an optional rewrite offered as a separate suggestion (`mirror.allow_rewrite_offers`). The original always stays visible; Mirror never removes an item (fixed). This keeps the owner's accept record in 11 unfiltered.
- **Gateway:** the profile enters a prompt only if `mirror.model_source` allows a hosted model. Never in agent prompts.
- **Owner controls:** view, edit or delete rules; pause learning; wipe profile and embeddings.

### 6. I'm Feeling Lucky [PROPOSED]
Purpose: a timed all-hands brainstorm for stuck or open problems.
- **Trigger:** owner only, with a prompt. The time box is `lucky.run_minutes`; runs per day are capped by `lucky.max_runs_per_day`.
- **Participants:** agents selected by `lucky.participation`. Each gets a recorded random persona seed. Opted-in agents are woken once at start by the 06 `brainstorm` wake reason, not by a 13 override (C08), and each is capped at `lucky.max_ideas` ideas.
- **Output:** an ephemeral `brainstorm` GC. Ideas are entries tagged `idea`, with source_trust `agent`. Reactions are not votes.
- **No execution:** an idea never becomes work. Under `lucky.cards_from_ideas`, the server creates Backlog cards (12) only for ideas the owner marked.
- **Security:** external text taints the session (C06); a secret-shaped string quarantines the idea.

### 7. Playtest Mode [PROPOSED]
Purpose: UX feedback from persona players, within what agents can perceive.
- **Tier A (autonomous):** Godot `--headless` scenarios with state assertions and Luau static checks (Bazaar.Soul.md section 2). Personas read state dumps, never pixels.
- **Tier B (human-assisted, Bazaar.Windows only):** the owner or a tester plays a build. The client records timestamped inputs and, at `playtest.capture_interval_seconds`, captures the game window only. Snapshots upload through the authenticated blob surface in 02 (C15). A tool-less reader session writes UX notes. Roblox Studio is Tier B only. Whether window capture is an allowed client-side exception is Q3.
- **Limits:** playtests never edit project files. Findings become Backlog cards only when the owner marks them. Persona scripts run in a no-secrets VM profile with egress denied (16, 17). Hosted vision is off unless `playtest.hosted_vision` is set.
- **Reputation:** notes weigh 0 until an owner-accepted card traces to them.

### 8. Sidewalk [name DECIDED; behaviour PROPOSED]
Purpose: the casual GC where social behaviour lives. The General Bazaar GC is the default Sidewalk.
- **Type** `sidewalk` (registered in 01): no Kanban binding, no task routing.
- **Wake:** mentions and owner messages wake. Other messages reach the digest; the Skimmer may promote one early under the 13 cap (C08). The daily token cap per agent is `sidewalk.daily_token_cap`, drawn from the 15 quota table.
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

### Settings [PROPOSED]
Registered in the doc 03 settings registry (round 13). Defaults are safe; operators change them in the setup wizard or advanced settings.

| Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `ward.min_stay_minutes` | int (minutes) | 60 | install | operator | Tier 2 (a lower floor loosens recovery) |
| `ward.admission_mode` | enum: automatic, owner_only | automatic | install | operator | Tier 2 |
| `ward.discharge_by` | enum: owner, owner_or_clean_reviewer | owner_or_clean_reviewer | install | operator | Tier 2 |
| `ward.curfew_behaviour` | enum: queue_next_shift, admit_now | queue_next_shift | install | operator | Tier 1 notice |
| `vent.required_before_card` | bool | true | install | operator | Tier 1 notice |
| `vent.publish_target` | enum: internal_general_gc, or an operator target from 36 | internal_general_gc | install | operator (needs counsel for external targets) | Tier 2 per post; target change Tier 2 |
| `what_if.reputation_weight` | decimal 0 to 1 | 0 | install | operator | Council may propose (rule 2 of section 0) |
| `replay.default_view` | enum: current, history | current | install, per-viewer override | any member (client preference) | none |
| `mirror.signal_kinds` | list | accept/reject, owner edits, review comments, Council tie-breaks, explicit statements | owner | owner | none |
| `mirror.evidence_threshold` | int | 5 | owner | owner | none |
| `mirror.allow_rewrite_offers` | bool | true | owner | owner | none |
| `mirror.model_source` | enum: local, operator_approved_hosted | local | install | operator (needs counsel for hosted) | Tier 2 |
| `lucky.run_minutes` | int (minutes) | 20 | install | operator | none |
| `lucky.max_ideas` | int per agent per run | 5 | install | operator | none |
| `lucky.max_runs_per_day` | int | 1 | install | operator | none |
| `lucky.participation` | enum: on_shift_or_opted_in, opted_in_only | on_shift_or_opted_in | install | operator | none |
| `lucky.cards_from_ideas` | enum: owner_marked, none | owner_marked | install | owner | none |
| `playtest.capture_interval_seconds` | int | 60 [PROPOSED] | install | owner | none |
| `playtest.hosted_vision` | bool | false | install | operator (needs counsel) | Tier 2 |
| `sidewalk.daily_token_cap` | int tokens per agent | the 15 quota-table value | install | operator | none |

Fixed, not settings: owner-only Lucky trigger, the Mirror rule that nothing is removed, host-side pushes, and the taint rules.

## Behaviour and edge cases
- **Erasure (C01):** vent drafts, Mirror signals and profile payloads, and playtest snapshot blobs are personal-data candidates. Erasure destroys the key and appends a tombstone; structure and timestamps stay. `fork_link` rows are metadata. Backup retention equals the erasure window (33). Counsel confirms legal sufficiency.
- **Vault material:** replayed memory and Vault commits are personal-data candidates inside Markdown and git history. That answer is open (18, 33).
- **Ward versus wake:** a ward admission removes Tier 2 capabilities and queues wakes; it does not drop inbox items (06).
- **Lucky cap:** the run-scoped wake ends when the time box (`lucky.run_minutes`) does.

## Testing
No real models or displays. Scripted fake gateway; real Postgres.
- Ward: trigger matrix; every loop-guard trip admits; admission removes Tier 2 capabilities; discharge follows `ward.discharge_by`.
- Sanitizer: golden corpus of planted names, emails, tokens and paths; zero leaks.
- Vent: hard-tainted draft held; soft-tainted draft held only with a tainted source; public path refused without approval. Erasure tombstone makes a vent draft unreadable.
- What If: changed metric spec aborts.
- Reader session: output validated against schema; untrusted text never reaches the actor session.
- Replay: rebuild-from-zero equals live state at N; DM hidden from non-members; fork writes no messages.
- Mirror: wipe removes profile and embeddings; annotate-only leaves the original item visible; rules below `mirror.evidence_threshold` do not apply.
- Lucky: recorded seed reproduces assignment; wake arrives through the 06 reason, not a 13 override; ideas beyond `lucky.max_ideas` are refused.
- Playtest Tier B: window-only capture; project hash unchanged. Avalonia.Headless for each UX surface in rule 5.

## Work packages
1. Sanitizer and golden corpus. No dependencies.
2. Ward state machine on the 09 BREAK state, with triggers. Depends on 09, 06.
3. Vent drafts and publish gate. Depends on 1, 15 tiers, 04 encryption key table.
4. Sandbox VM profile and What If runner. Depends on 16, 17.
5. Replay projector and `fork_link`. Depends on 01, 04, 18.
6. Mirror signals and profile (server only). Depends on 01, 11.
7. Lucky controller and the `brainstorm` wake reason. Depends on 06, 12, 13 (promotion cap only).
8. Playtest Tier A (headless, depends on 20) and Tier B (capture, depends on 02 blob surface and 28).
9. Sidewalk type defaults and wake rule (01, 13). Client views with Avalonia.Headless (27).

## Conflicts
Remaining only. Cross-doc fixes are listed for the owning doc.
1. **Vent target (36).** 36 must name the operator-set posting targets that `vent.publish_target` refers to.
2. **Reader-session primitive (15).** Playtest notes, Mirror, Lucky and Sidewalk rely on a tool-less reader session. 15 must define it and its hand-over format (C18).
3. **Presence labels (09).** 09 must adopt the eight labels and overlays (C43); WARD is an overlay.
4. **Erasure in Vault history (18, 33).** Memory and commits holding personal data cannot be erased by key destruction. Open (C38 consequence).
5. **Client capture (17, 28).** Playtest window capture is not in the ratified client-side exceptions (C17). See Q3.

## Questions for the owner
1. Fork: copy messages or link read-only? Default: link, plus a Vault branch. Copying changes the fork data shape, so it stays a question.
2. Lucky wake: the 06 `brainstorm` wake reason, once per opted-in agent per run, instead of any 13 override? Default: yes. This sets the wake-reason set in 06, so it stays a question.
3. Playtest Tier B capture (window only, started by the owner) is not in the C17 list of client-side exceptions. Ratify it as a Bazaar.Windows exception, Tier B only? Default: yes.
4. Mirror profile storage: encrypted Postgres (default) or Vault Markdown, as the C38 principle would suggest? Default: Postgres, because a git-history profile is hard to erase and it is a model, not a written artifact. Counsel to confirm. This is a storage-layout choice, so it stays a question.
5. Vent drafts: private GC entries (default) or Vault notes under the C38 principle? Default: GC entries, encrypted per person. This is a file and storage layout choice, so it stays a question.

MOVED TO SETTINGS: ward.min_stay_minutes, ward.admission_mode, ward.discharge_by, ward.curfew_behaviour, vent.required_before_card, vent.publish_target, what_if.reputation_weight, replay.default_view, mirror.signal_kinds, mirror.evidence_threshold, mirror.allow_rewrite_offers, mirror.model_source, lucky.run_minutes, lucky.max_ideas, lucky.max_runs_per_day, lucky.participation, lucky.cards_from_ideas, playtest.capture_interval_seconds, playtest.hosted_vision, sidewalk.daily_token_cap (DECISIONS-LOG round 13)

ANSWERED: erasure is per-person encrypted payloads with key destruction and tombstones from day 1; legal sufficiency still needs counsel (DECISIONS-LOG round 2, C01).
ANSWERED: Sidewalk wake rule follows the general rule: mentions and owner messages wake; Skimmer promotion is capped (DECISIONS-LOG round 3, C08).
ANSWERED: loop-guard trip goes to the Psych Ward on any trip, with no separate 15-minute break (DECISIONS-LOG round 9, C34).
