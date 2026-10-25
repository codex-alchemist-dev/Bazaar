# Cross-doc fix pass (read first)

Every doc in `docs/design/` was reconciled with `DECISIONS-LOG.md` one at a time, and the agents found contradictions between docs. This pass fixes them and applies the owner's **configurability rule (DECISIONS-LOG round 13)**. You edit ONLY the doc named in your assignment.

## Read

1. `/home/user/Bazaar/docs/design/DECISIONS-LOG.md` in full (authoritative; round 13 is new).
2. This file: the general rules below and the section for your doc.
3. Your doc. Open another doc only to copy an exact name (table, interface, enum value) you must match.

## Part 1: apply your doc's fix list

Apply each item in your doc's section below. Each item names the doc that owns the contract; use that doc's names exactly. If an item turns out to be already done, skip it. If an item contradicts DECISIONS-LOG, the log wins: leave a line in Conflicts instead.

## Part 2: apply the configurability rule to your Questions for the owner

For each open question, decide which kind it is:

- **Setting**: a plain value or policy choice (a default, limit, cap, cadence, window, threshold, timeout, list, label mapping, on/off feature, per-project or per-agent preference). Remove it from the questions. Add a row to a `### Settings` table in your Data and interfaces section, with columns `Key | Type | Default | Scope (install/project/agent) | Who may change | Change needs approval?`. Use the question's recommended default as the default. Keys are lower-case dotted, prefixed by your area (for example `kanban.max_blockers`). Doc 03 owns the settings registry.
- **Stays a question**: anything that (a) changes a DECIDED item or a security invariant (hard taint, key custody, deny-by-default model policy, no subscription credentials, owner reads all DMs; a setting may tighten these, never loosen below the shipped floor), (b) changes a schema, wire format, or file layout (one answer needed), (c) is a naming, product-scope or design-intent question only the owner can answer, or (d) asks the owner to confirm "Claude's reading" of a decision.
- **Legal or privacy exposure** (erasure, retention, public posting, data leaving the install): becomes a setting whose options are documented and whose default is the most protective option, marked "needs counsel". Keep a one-line question only if the options themselves are undecided.

Renumber the remaining questions. Add one line after them: `MOVED TO SETTINGS: <comma-separated keys> (DECISIONS-LOG round 13)`.

## General rules

- Keep the doc's skeleton and labels (DECIDED / PROPOSED / OPEN, verified / unverified). Never upgrade a claim's label.
- Do not invent product decisions. A new default you need is `[PROPOSED]`.
- Update the status line to `Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: <count>`.
- Conflicts: remove items this pass resolves; keep the rest.
- Aim at about 2,000 words; trim sketches and repeated decided-input text before questions or decisions.
- No secrets, real hostnames or personal data. Do not commit or push. Do not touch any other file.

## Report back

File path, number of questions before and after, the settings keys you added, fix-list items you could not apply and why, and any NEW conflict (doc number plus the exact claim).

---

## Fix lists by doc

### 01-gc-data-model.md
- Provenance enum: use doc 15's values (`owner, system, agent, member-human, external, quarantined`). Remove any `external_untrusted`, `agent_tainted` or `human_verified` values.
- Register GC types used by other docs: `ward`, `vent`, `sandbox`, `brainstorm`, `sidewalk`, `pr`, `inbox`, `thinking` (and confirm `external_mail` as an event kind, doc 22). Doc 22 uses `external-mail`; pick `external_mail` and note it.
- Register event kinds `council.open`, `council.reveal`, `council.transition`, `council.decision` (doc 10), `message_edit`, `quarantine`, `handover` (doc 15).
- Add `gc_access_request` (DM access requests, doc 32) and say it emits a `system` event.
- Kanban authority: doc 12's task service is the only writer of task state and writes the GC system lines; remove any "Kanban projector" consuming `task_update` / `pr_event` entries.
- Remove any remaining single `entry`-table wording.

### 02-wire-protocol-and-sync.md
- Register stream names other docs use: `approvals` (15), `graph:<id>` (19: `GraphSnapshot`, `LayoutFrame` entries; `FocusRequest` command), `pane` (20), `board` (12, snapshot plus revision resync).
- Remove any sentence saying the socket is the only client-server channel; the authenticated HTTP surface exists (C15).
- Wake hint: kind only, no gc id or seq (C41). Check every example.
- Keep the edit cursor (`ev`, `editAfter`) as [PROPOSED] with its question; that is a wire-shape question and stays.

### 03-server-host-and-providers.md
- Add a short `### Settings registry [PROPOSED]` section (DECISIONS-LOG round 13): typed key, default, scope (install / project / agent), who may change it, whether a change needs approval (doc 15 tier), audit entry on change (doc 15 `audit_log`), surfaced in the setup wizard and advanced settings (docs 27, 32). Settings may tighten security floors, never loosen them. Values live in Postgres; bootstrap values (KEK reference, DB connection) stay in host config.
- `job` table leases: keep as [PROPOSED] inline; doc 06's `agent_lease` is for agent work claims.

### 04-postgres-schema-and-migrations.md
- `gc` uses `head_seq` (last assigned seq), not `next_seq`; fix RETURNING semantics to match doc 01.
- `gc_member` key is `principal_id` (humans are principals), not `agent_id`.
- `gc_message`: no `current_version` column (insert-only; highest version is current).
- Provenance columns (`source_trust`, `tainted_at_write`, `taint_sources`, `derived_from`) live on `gc_message_version` per doc 15 (and `source_trust`, `tainted_at_write` on `gc_event` per doc 01). Reconcile with doc 01 and state one placement.
- Event kinds: include `message_edit`, `external_mail`, `quarantine`, `handover`, `council.*`.
- Per-person encryptable fields list: add `council_ballot.reasoning`, `council_ballot_ver.reasoning`, `community_action.body`, `bot_profile.contact`, mail bodies (22), vent and Mirror payloads (26).
- `gw_usage` / `usage_hour` partitioning: state "unpartitioned in v1 [PROPOSED]; revisit after volume is measured"; no time partitioning (C10).
- The DEK / `person_key` table: keep the GC per-person key table here; Soul DEKs and Passport DEKs are doc 14's.

### 05-model-gateway.md
- `gw_usage.input` is uncached input and excludes cache reads; say so (doc 23 relies on it).
- Pipeline text must say it writes `gw_usage` (not "one UsageRecord").
- SpendCapped: reroute to another allowed source (C14); vacation only when no allowed source remains (doc 09).
- Add a gatekeeper (Skimmer scorer) policy entry (doc 13).
- Policy editing (Q5): the owner edits model whitelists; whether a change is a Council "bigger change" stays tied to the C32 FOLLOW-UP.

### 06-agent-runtime.md
- Remove `tz` and `schedule` from `agent` if still present (doc 09 owns `agent_schedule`).
- `inbox_item` gets a unique key on `(agent_id, gc_id, seq, kind)` for idempotent enqueue (doc 13).
- Wake reasons: add `brainstorm` (Lucky mode start, doc 26) and a vote-request reason for Council ballots (doc 10; use `assignment` if you prefer, but name it).
- Lucky mode uses the `brainstorm` wake reason, not a Skimmer override [PROPOSED].
- Thinking GCs: nested under each working GC (C11); remove "one Thinking GC per agent".
- Skimmer: it may promote a digest item early under its own cap (C08); remove "may only move an item from wake now to digest". State whether the 30-per-hour total wake cap includes Skimmer-promoted wakes [PROPOSED: yes, the scored cap of 20 sits inside it].
- Define the session key that doc 15's `session_taint` references (`turn.session_id` or equivalent), and the `turn.role` actor|reader field 15 uses.
- Subagent limits: depth 2, 3 concurrent are runtime delegation limits; doc 17's coding subagents use depth 1 inside that.
- No 15-minute break after a loop-guard trip; admission to the Psych Ward (C34).

### 07-soul-system.md
- Talent review, hiring, promotion, retirement: Council votes (C32), not "owner decision".
- Soul export never includes Preferences or personal data (doc 26 Mirror exclusion); fix any "exports Preferences" line.
- Decision index: decisions are `vault_docs` rows with `type=decision` (doc 18); no separate table.

### 08-memory-system.md
- Section on erasure: backup retention equals the erasure window (C01); remove "until expiry" wording.
- Lessons are Vault memory notes (doc 07 no longer has a `lessons` table).
- Hive copies and Genetic Memory Transfer candidates are Vault Markdown (doc 25); no `memory_items` scope.
- Keep T0-T3 names and point the provenance mapping to doc 15's table.

### 09-work-life-simulation.md
- Remove any 15-minute BREAK or digest-only pause after a loop-guard trip; loop-guard trips go to the Psych Ward (C34).
- Drop any own `inbox_item` copy; use doc 06's.
- Base presence label for on-shift idle / waiting / drafting: make it a setting (`presence.idle_label`, default `working` with an `idle` overlay) [PROPOSED], and note doc 23 Q14.

### 10-council-and-voting.md
- A Council vote can propose a Constitution change (doc 24); whether it is a "bigger change" follows the C32 FOLLOW-UP.
- Reputation never weights votes in v1 unless the owner answers Q1 otherwise; make the weighting factor a setting (`council.reputation_weight`, default 1.0 = none) per round 13.

### 11-reputation-and-peer-review.md
- Remove automatic promotion at LCB >= 0.6 without a vote; the scorer nominates and the Council votes (C32).
- Blocker-caused waits (doc 12 dependencies) do not count as reputation failures.

### 12-kanban-and-dependencies.md
- Board resync: snapshot plus revision on the `board` stream registered in doc 02.
- Done for a PR card = merged (C28); check no "Done = reviewer approval" text remains.

### 13-skimmer-bouncer.md
- Lucky mode start: use doc 06's `brainstorm` wake reason; mark `gate_run_override` as an alternative, not the default [PROPOSED].
- Express the fake scorer through doc 05's ModelEvent seam (doc 34).

### 14-identity-service-and-passport.md
- GitHub App installation token lifetime: "unverified until test T4" (doc 21), not "verified".
- Owner merge credential: owner-class, either a dedicated owner GitHub App with merge rights or the owner's linked credential (round 12).
- Owner signed approval for merges needs a device-held signing key; name the key class [PROPOSED] or point to doc 32, and keep it a question if undecided.
- Coding worktrees never sit on the cookie-bearing Passport folder (doc 17); the cookie mount is not reachable from the Exec-tier shell.
- Own the Soul DEK and Passport DEK tables; doc 04 owns the GC per-person key table.

### 15-security-taint-and-approvals.md
- Remove any "reader/actor not built in v1, role field reserved" wording; the split is in v1 (C18, Claude's reading still to confirm).
- Register hand-over schemas used by others: `mail_triage` (22), community reader hand-over (36).
- Shipped defaults: new account creation = Tier 2; unverified platform = Deny (C21, doc 36).
- Quota table rows: Skimmer scored wakes (20 per agent per hour, doc 13's own limit, reference row), community posts (5 per platform per day), GitHub writes (30/min per install), public new issues/PRs per repo per day (one value shared by docs 21 and 36: [PROPOSED] 1 per repo per day). Mark values as settings.
- Mount: state whether cookie profiles in Soul folders are included in backups (doc 33 Q15) — [PROPOSED] excluded by default.
- Confirm a prompt rewrite built from external content keeps `source_trust = external` (doc 31).
- Subagent output maps to `agent` provenance (doc 25); no `subagent` value.

### 16-agent-vms-and-environments.md
- The second inter-VM share is the work share for coding worktrees (doc 17), not the Passport folder.
- Agent terminal transport: the PTY guest messages here are what docs 17 and 30 point to; say so.

### 17-coding-agent-harness.md
- Drop any "Browser VM" role (C22); browser profiles live in the two VMs.
- Command timeouts: reference doc 16's exec limits (15 min default, 2 h max) instead of a 10-minute timeout.
- Use doc 16's `IGuestChannel` as the only guest wire, with `IWorkspace` as a facade.
- `CapabilityClass` naming and doc 15 tier names throughout.
- Classify sessions: a session that reads stranger repository files is a reader session (hard taint).
- Coding subagents: depth 1 within doc 06's runtime limit.

### 18-vault.md
- Add a `draft` document type (Constellation drafts, doc 19) [PROPOSED], or say how drafts are held.
- Git DAG index table owner: this doc for the Vault repo, doc 17 for code repos [PROPOSED].
- VaultWriter assigns the decision number NNNN and returns it (doc 10).
- Read scope for review rationale notes (doc 11): author, owner, linked GC.
- Vault push cadence (sets doc 33's Vault RPO) as a setting.
- Twin pages folder name (doc 25).
- Charter text as a Vault file with SHA-256 in `soul_versions` (doc 07 Q12): record as [PROPOSED].

### 19-constellation-and-git-solar-system.md
- Match doc 18's type list (including `draft` if 18 adds it).

### 20-engine-integration-api.md
- No fixes from other docs. Apply Part 2 only.

### 21-github-integration.md
- Public-write caps: take values from doc 15's quota table (one value shared with doc 36); remove the 2-per-agent-per-day numbers.
- Merge-request path from clients (docs 28, 29, 30): server squash-merges on the owner's signed approval with the `Signed-off-by` line; both credential options (round 12).
- Fork-and-PR: keep it as the owner decided in round 3 (risk accepted, unverified until S22). Write "DECIDED (round 3, risk accepted)" only if the log says so; otherwise "kept as decided".

### 22-email-subsystem.md
- Event kind name: `external_mail` (doc 01), with `source_trust = external`.
- Reader model is doc 15's hand-over primitive (`mail_triage` schema).

### 23-observatory-and-usage.md
- Presence base label for idle: point to setting `presence.idle_label` (doc 09).
- `gw_usage` / `usage_hour`: unpartitioned in v1 per doc 04.

### 24-company-systems-core.md
- No fixes from other docs beyond consistency with doc 06's `PushAsync`/`PopAsync` names. Apply Part 2.

### 25-company-systems-knowledge.md
- Hive grant review (30 days, then automatic under the grant): make it a setting (`hive.grant_review_days`, default 30) per round 13; the existence of automatic grants stays a question only if the owner must approve the mechanism.
- Talent Pool hiring runs as a Council `hire` motion (doc 10).

### 26-fun-systems.md
- Lucky start: doc 06's `brainstorm` wake reason.
- Playtest client-side window capture: stays an owner question (it would extend the ratified client-side exceptions, C17).

### 27-client-architecture.md
- Action chips are deterministic (no model); the NPU writes only an optional prose summary (doc 31).
- Own the cached offline profile and the minimal built-in shell (C27).
- Surface the settings registry (doc 03) in advanced settings and the setup wizard.

### 28-windows-client.md
- Action chips deterministic; the local NPU summary is optional (doc 31).
- Studio bridge: a per-call grant ID that points to a durable `engine_grants` row or approval (doc 20), not a fresh grant per call.
- Studio writes: follow doc 20's default (agents write freely to a designated dev place); keep the owner question as doc 20's Q5.
- Offline dashboard: cached profile and derived counters only.

### 29-android-client.md
- Remove any `dataSync` keep-alive foreground service (C41).
- Folded-layout merge: make it a setting (`android.merge_from_folded`, default on, fresh biometric approval required as a floor that cannot be loosened).

### 30-linux-client-and-packaging.md
- Remove any `dataSync` keep-alive foreground service mention for Android (C41).

### 31-local-coworker-npu.md
- No fixes from other docs. Apply Part 2.

### 32-remote-access-auth-and-privacy.md
- Define (or explicitly defer to doc 14) the device-held signing key for owner merge approvals [PROPOSED].

### 33-backup-dr-and-export.md
- Cookie profiles in snapshots: [PROPOSED] excluded by default, as a setting (doc 15).
- Vault push cadence: reference doc 18's setting.

### 34-testing-strategy.md
- Fake scorer and scripted gateway go through doc 05's ModelEvent seam.

### 35-spikes-plan.md
- S14: measure the absolute NUC budget from doc 19 (layout tick <= 8 ms at 10k nodes), not only a relative cloud figure.
- Add a spike for the native Android diff/edit surface (doc 29; no Monaco on Android).

### 36-community-and-external-platforms.md
- Public-write caps: one value from doc 15's quota table, shared with doc 21.
