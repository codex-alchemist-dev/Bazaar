# Conflict register for the design set

Status: DRAFT for owner review (2026-10-25) · Consolidates docs 01 to 36 in this folder · 45 entries: 14 BLOCKER, 21 SHAPING, 10 MINOR

The 36 design drafts were written in parallel, so they contradict each other and, in places, the decisions in force (`CLAUDE.md`, `docs/deep-dives/`). This register lists only CROSS-CUTTING items: each one touches two or more docs, or a decided item. Where two docs already agree, nothing is listed. Per-doc questions stay in each doc's own "Questions for the owner"; answering a register question normally settles several of them at once.

## How to read an entry

- **Severity.** BLOCKER: decide before the affected code is written. SHAPING: changes a design materially. MINOR: wording, defaults or naming.
- **Docs.** File numbers (01 = `01-gc-data-model.md`). "CLAUDE.md", "Server.md", "Soul.md", "Bazaar.md" etc. mean the deep-dive docs in `docs/deep-dives/`.
- **Evidence.** verified / secondary / unverified, as the source docs label it. Nothing here upgrades a claim.
- **Recommended.** My recommendation with one sentence of reasoning. It is a proposal, not a decision.
- **Ask.** A multiple-choice question, ready to put to the owner. The recommended option is marked (rec).
- Sorting: BLOCKER, then SHAPING, then MINOR; inside a severity, the entries touching the most docs come first.

## Index

| ID | Title | Severity | Docs |
|---|---|---|---|
| C01 | Erasure vs append-only log, backups and sealed data | BLOCKER | 01 04 06 07 08 10 14 22 26 32 33 |
| C02 | Passport mounting vs the Rule of Two | BLOCKER | 07 08 11 14 15 16 17 33 |
| C03 | VM topology: OPEN or decided | BLOCKER | 03 06 07 15 16 17 33 35 |
| C04 | Competing runtime tables (agent, turn, inbox, lease, quota) | BLOCKER | 03 04 06 07 09 13 23 24 |
| C05 | Secret and token custody | BLOCKER | 03 05 14 17 21 23 27 33 |
| C06 | Taint scope vs approval fatigue | BLOCKER | 12 14 15 17 21 22 36 |
| C07 | Pub/sub provider: Garnet/Valkey vs Postgres NOTIFY | BLOCKER | 02 03 04 11 12 23 35 |
| C08 | What may wake an agent, and who owns the digest | BLOCKER | 06 09 13 21 24 26 |
| C09 | Shape of the GC log tables | BLOCKER | 01 02 04 15 22 |
| C10 | Sequence numbers vs partitioning vs restore rollback | BLOCKER | 01 02 04 30 33 |
| C11 | Thinking GC, peer-visible thinking, and independent voting | BLOCKER | 01 06 10 25 26 |
| C12 | Git engine: LibGit2Sharp (decided) vs the git CLI | BLOCKER | 17 18 21 35 |
| C13 | Fork-and-PR and issue creation vs GitHub docs | BLOCKER | 14 21 35 36 |
| C14 | Claude lane: API keys only vs FreeLLMAPI | BLOCKER | 05 07 09 35 |
| C15 | Wire protocol gaps: second channel, blobs, stream registry | SHAPING | 01 02 12 15 18 19 20 21 22 23 26 29 32 |
| C16 | Duplicate table definitions (04 outline vs owner docs, audit, approvals) | SHAPING | 04 07 08 10 11 12 14 15 16 17 18 32 |
| C17 | "Server does all compute" vs client-side exceptions | SHAPING | 13 19 20 26 27 28 29 30 31 |
| C18 | Reader/actor split: OPEN but assumed | SHAPING | 08 13 15 22 25 26 34 36 |
| C19 | GC type list and well-known GCs | SHAPING | 01 06 12 21 22 24 26 29 |
| C20 | Trust and taint vocabularies | SHAPING | 01 07 08 15 21 22 25 |
| C21 | Approval-tier scales disagree | SHAPING | 07 14 15 17 21 22 36 |
| C22 | Guest protocol, VM interfaces and extra VM roles | SHAPING | 03 14 15 16 17 20 26 |
| C23 | Licensing, signing and host-edition constraints | SHAPING | 03 04 16 18 28 30 31 |
| C24 | Decision records: shape, path and writer | SHAPING | 01 07 10 18 24 25 |
| C25 | Owner merge path and owner credential custody | SHAPING | 12 14 17 21 28 32 |
| C26 | Monaco hosting vs paid Avalonia tiers | SHAPING | 19 27 28 30 35 |
| C27 | Server-driven layout vs fold layout vs offline cold start | SHAPING | 02 19 27 28 29 |
| C28 | Kanban authority, Done vs merge, replay cursor | SHAPING | 01 10 12 17 21 |
| C29 | Model-call seam: ModelEvent vs IChatClient | SHAPING | 05 06 13 17 34 |
| C30 | Mail and webhook ingress vs "no inbound on the home host" | SHAPING | 03 21 22 32 |
| C31 | Terminal location and default-terminal registration | SHAPING | 27 28 30 35 |
| C32 | Reputation weights, promotion and Talent eligibility | SHAPING | 07 10 11 25 |
| C33 | Usage table, cache columns and the cost chip | SHAPING | 04 05 09 23 |
| C34 | "Mandatory walk" has three definitions | SHAPING | 06 09 26 |
| C35 | Interrupt contract | SHAPING | 06 09 24 |
| C36 | Divergent numeric defaults across docs | MINOR | 06 11 13 15 17 21 22 36 |
| C37 | CLAUDE.md gaps: new names, tools, the verification rule | MINOR | 03 17 27 28 30 31 34 |
| C38 | Company knowledge location, cover reads, Hive grants | MINOR | 08 09 18 25 |
| C39 | Embedding profile and dimension | MINOR | 04 05 08 18 |
| C40 | Status and health endpoints, protocol version scheme | MINOR | 02 03 23 30 |
| C41 | Push payload, wake kinds, foreground-service claim | MINOR | 02 23 29 30 |
| C42 | Sign-in page scope vs CLAUDE.md wording | MINOR | 02 15 32 35 |
| C43 | Presence state list | MINOR | 09 23 26 |
| C44 | Migrations at startup vs explicit step | MINOR | 03 04 30 |
| C45 | `calendar_event` ownership | MINOR | 09 24 |

---

## BLOCKER

### C01. Erasure vs append-only log, backups and sealed data
**Severity** BLOCKER · **Docs** 01, 04, 06, 07, 08, 10, 14, 22, 26, 32, 33 (DM-read side issue: 01, 10, 32)

**Conflict.** 01: "Rows are never UPDATEd or DELETEd; the app role has INSERT and SELECT only on the entry tables" and "legal erasure cannot be done by the app". 32 §9: "personal fields stored as per-person encrypted payloads; erasure destroys the key and appends a tombstone". 04 C1: "its legal sufficiency is unknown and backups keep keys until expiry". 33: "Erasure therefore completes only when every snapshot holding that DEK has expired" (35 days in 04 Q7, "until expiry" in 08 §9). 22 Q11 purges raw quarantined mail after 90 days; 06 Q14 prunes `turn_step` bodies after 90 days; 26 lists vent drafts, Mirror signals and playtest snapshots as personal-data candidates; 10 C6 notes sealed ballots are readable from backups. Related: the owner reads all DMs (01) while 32 Q8 proposes a visible marker for coworkers.

**Evidence.** Legal sufficiency of key destruction: unverified, needs counsel (Server.md Q17, OPEN).

**Recommended.** Make `body` and personal fields per-person encryptable in the first migration (separate key table, backup retention equal to the erasure window), keep structure, authorship and timestamps, treat quarantined raw mail as metadata entry plus purge, and settle the legal position later. Reason: encryption cannot be retrofitted into an append-only log without rewriting it.

**Ask.** How should the GC log handle erasure? (a) Per-person encrypted fields from day 1, key destruction on request (rec). (b) No encryption; documented retention exemptions only. (c) Wait for counsel; leave the schema unchanged until then. (d) Allow rewrite-redaction (ends append-only).

### C02. Passport mounting vs the Rule of Two
**Severity** BLOCKER · **Docs** 07, 08, 11, 14, 15, 16, 17, 33 (and Soul.md §1, §2)

**Conflict.** Soul.md §1 (owner, 2026-10-24): "Passport data may be mounted into the VM as a per-agent scoped folder". 15: "Mount holds only low-sensitivity items: handles, service metadata, session-cookie profile" and hard taint "detaches the Passport mount". 14: "the scoped folder holds metadata by default ... Default: no class", with cookies injected by the broker (so 15 mounts cookies, 14 does not). 16 Q1: "Recommend brokering by default". 07 C1: "assumes the research rule (memory only, read-only), which overrides a DECIDED item". 08: "proposes no mount, which amends Soul §2". 11: reputation is read through the platform API only. 17 puts browsing in a "Browser VM" with no Passport. 16 Q2 also adds a second share to Soul.md's "one scoped folder"; 33 snapshots Soul folders.

**Evidence.** Threat-model advice against mounting: research (secondary). Owner accepted the risk.

**Recommended.** Keep the mount, but only metadata (service names, handles, expiry), memory and reputation through server APIs, cookies by broker injection, detach on hard taint, and list the two shares of 16 Q2 as an explicit exception. Reason: honours the decision while removing the credential leg of the Rule of Two.

**Ask.** What may the per-agent mounted folder contain? (a) Metadata only (rec). (b) Metadata plus a session-cookie profile, detached on taint (15). (c) Nothing; brokering only (16). (d) A named credential class with a short TTL.

### C03. VM topology: OPEN or decided
**Severity** BLOCKER · **Docs** 03, 06, 07, 15, 16, 17, 33, 35

**Conflict.** CLAUDE.md: "Agent VM topology is OPEN: benchmark nested WSL2 vs two VMs per agent before building on either." Soul.md §2 (CHANGED 2026-10-24): "Each agent gets two VMs: a Windows VM and a Linux VM", and open question 6: "decided 2026-10-24: two VMs per agent". 16 treats it as decided and asks (Q13) to "Drop the single-VM nested comparison". 35 Q4: "Benchmark both ... despite the Soul doc's 'decided' text". 03 C1: "`IVmBackend` cannot be fixed until this is resolved." 07 and 33 assume the pair; 06 assumes only a workspace API; 15 notes "nested-VM networking (Soul benchmark OPEN)".

**Evidence.** Nested-VM performance unmeasured; pairs per 32 GB host unverified (16 estimates about 4); Hyper-V behaviours partly unverified.

**Recommended.** Record the two-VM pair as decided in CLAUDE.md and keep spike S8 for sizing only. Reason: the owner's dated CHANGED entry is specific and every downstream doc already builds on it.

**Ask.** Which topology is in force? (a) Two-VM pair decided; benchmark sizing only (rec). (b) Still OPEN; run the nested-vs-pair benchmark first. (c) Pair decided, but the Windows VM is created only for agents with Windows-only tooling (16 Q3, 17 Q7).

### C04. Competing runtime tables (agent, turn, inbox, lease, quota)
**Severity** BLOCKER · **Docs** 03, 04, 06, 07, 09, 13, 23, 24

**Conflict.** `inbox_item` is defined twice: 06 (`kind, gc_id, seq, priority, state, claimed_by, claimed_at`) and 09 (`gc_seq, payload, status, claimed_until`); 13 adds `digest_items`. Leases: 03 `agent_turn` plus `job` with `lease_owner/lease_until`, 06 `turn`, `turn_step`, `agent_lease`. `agent`: 04 (`timezone, schedule, status`), 06 (`soul_ref, state, model_policy_id, tz, schedule jsonb`), 07 (`souls`), 09 (`agent_schedule`, `availability`), 23 (`agent_activity`; "04 `agent.status` would mix availability with activity"). Lifecycle states: 06 Created/Onboarding/Active/Resting/Retired vs 07 active/paused/retired. Quota: 09 `quota_state` vs 05 `gw_quota_state`. Model whitelist: 04 `agent_model_allow` vs 05 `agent_model_policy`.

**Evidence.** Design-level; no external source.

**Recommended.** One owner per table: 06 owns agent, turn, step, lease and `inbox_item` (09 and 13 add kinds, not tables); 07 owns Soul versions; 09 owns schedule and availability; 05 owns quota and policy; 23 owns activity. Reason: four first migrations otherwise create the same table four ways.

**Ask.** Which doc is canonical for the agent runtime tables? (a) 06, others extend it (rec). (b) Split identity (07) from runtime state (06) with an explicit key between them. (c) Re-draft all after the contracts project exists.

### C05. Secret and token custody
**Severity** BLOCKER · **Docs** 03, 05, 14, 17, 21, 23, 27, 33

**Conflict.** 03: secrets are references (`env:`, `file:`, `dpapi:`, `systemd-creds:`) resolved by `ISecretResolver`. 05: `secret://bazaar/providers/...` resolved by an `ISecretStore` "owned by the Identity design; interim v1 store is a provider-per-OS". 14: the Identity Service "is the only holder of decryption keys". 23 reads GitHub App keys from `ISecretStore`. 27 defines a client-side `ISecretStore` (a different thing, same name). 21 C5: "17 has the VM push with a brokered token; 14 says no secret leaves the host. This doc moves pushes host-side. 17 needs amending." 33 C1: DPAPI machine-scope KEK vs recovery on new hardware (sealed export creates a second master-key copy).

**Evidence.** GitHub installation token lifetime of one hour: "verified" in 14, unverified per 21 C3.

**Recommended.** One server-side `ISecretStore` owned by 14; 03's resolver only bootstraps the KEK and database credential; provider and App keys become Passport-class credentials; pushes run host-side; rename the client interface. Reason: a single chokepoint is the only auditable one.

**Ask.** Who holds provider and GitHub App keys in v1? (a) Identity Service from the start (rec). (b) OS-protected files behind `ISecretStore` first (05 default), migrate later. (c) Env/file references only (03).

### C06. Taint scope vs approval fatigue
**Severity** BLOCKER · **Docs** 12, 14, 15, 17, 21, 22, 36 (also 29)

**Conflict.** CLAUDE.md taints on untrusted input (email, strangers' issues, web). 15: "Agent-authored GC text taints softly ... only hard taint blocks Tier 2 actions". 17 C3: "Applied fully, nearly every coding session is tainted"; 17 Q2 recommends taint "only when the source session was tainted". 36 C1: "a Tier 1 reply cannot be autonomous in the session that read the thread". 14: tainted sessions "cannot call Tier 1+ actions", 15 says Tier 2+. 12: a tainted session "needs owner approval for Done". 21: stranger comments "taint any reading session".

**Evidence.** "Always allow" fatigue is a documented failure mode (threat model, secondary).

**Recommended.** Hard taint only for `external` content; agent text is soft and propagates only from a tainted source session; Tier 2 gate on hard taint; fresh-session path with an approved summary (15 Q2). Reason: keeps the decided gate meaningful without freezing normal GC work.

**Ask.** What taints a session? (a) External content hard; agent text soft, only if its source session was tainted (rec). (b) All agent text hard. (c) External only; agent text never taints. (d) Build the reader/actor split first and approve everything until then.

### C07. Pub/sub provider: Garnet/Valkey vs Postgres NOTIFY
**Severity** BLOCKER · **Docs** 02, 03, 04, 11, 12, 23, 35 (CLAUDE.md, Server.md §3)

**Conflict.** CLAUDE.md lists "cache and pub/sub (Garnet on Windows, Valkey on Linux)". 02: needs "only an in-process dispatcher". 03 Q2 and 04 Q1: "NOTIFY only". 35: S2 (Garnet) "only if S3 fails". But 11: scores "cached in Valkey"; 23: "Pub/sub is Valkey on Linux or Garnet on Windows" for `presence` deltas; 12 WP8: event outbox "and the pub-sub provider".

**Evidence.** Garnet's StackExchange.Redis compatibility and Windows persistence: unverified (Server.md calls Garnet "candidate"). Postgres chat scale: no benchmark (S3 pending).

**Recommended.** v1 is Postgres LISTEN/NOTIFY plus an in-process dispatcher; `IRealtimeBus` stays optional; 11, 12 and 23 must not require Valkey or Garnet; amend CLAUDE.md. Reason: removes an unverified dependency from the first Windows host.

**Ask.** Pub/sub in v1? (a) NOTIFY only; Garnet/Valkey optional later (rec). (b) Garnet on Windows now, after spike S2. (c) NOTIFY for GC fan-out; Valkey/Garnet for cache and presence.

### C08. What may wake an agent, and who owns the digest
**Severity** BLOCKER · **Docs** 06, 09, 13, 21, 24, 26

**Conflict.** 06: "Nothing else wakes an agent: a message without a mention is only stored"; Q3 default: non-mention human messages never wake. 13: `critical` path wakes on "owner message"; `scored` path wakes unmentioned messages "under cap"; mode `off` = "every message wakes". 26: Sidewalk "mentions and owner messages wake"; Lucky is "a run-scoped override of 13" (26 C4: "13 has no run-scoped override"). 09 Q3: owner DMs wake during curfew. Digest: 06 every 2 simulated working hours (`inbox_item`), 13 every 30 minutes (`digest_items`).

**Evidence.** No source measures chat relevance or miss rates (13 C4); design-level.

**Recommended.** 06 owns wake reasons (interrupt, mention/DM, assignment, digest, schedule). 13 may only move items between wake-now and digest, so its `scored` wake becomes "promote from digest early, capped". One digest, one table, one cadence. Reason: 13 inherits 06's invariant that nothing is dropped.

**Ask.** May a message without an @mention wake an agent immediately? (a) No, except DM, assignment and a short critical list (rec). (b) Yes, when the Skimmer scores it above threshold, under a cap (13). (c) Yes for any owner message.

### C09. Shape of the GC log tables
**Severity** BLOCKER · **Docs** 01, 02, 04, 15, 22

**Conflict.** 01: one append-only `entry(gc_id, seq, kind, ...)` where edits are `edit` entries, plus `gc.head_seq`, `gc_member(principal_id)`, `mention`. 04: `gc_event` plus `gc_message` plus `gc_message_version`, `gc.next_seq`, `gc_member(agent_id)`, `gc_mention`, and Q6 "full copy per version". 02 matches 01 ("Edits are new log entries with their own `n`"). 15 adds a separate `gc_message_trust` table; 01 has `trust` and `tainted_at_write` columns. 22 adds `kind=external-mail`, `trust=untrusted-external`, which 01's kinds (message, edit, system, structured, quarantine) do not include.

**Evidence.** Design-level.

**Recommended.** Adopt 01's single `entry` table (02 and 33 already depend on it), keep 04's insert-only trigger and conventions, put trust columns on `entry`, and make kinds an extensible set. Reason: replay and restore (02, 33) are simplest when edits are ordinary entries.

**Ask.** Which log model? (a) 01's single `entry` table with edits as entries (rec). (b) 04's event, message and version tables. (c) 01's table plus a derived `entry_version` view.

### C10. Sequence numbers vs partitioning vs restore rollback
**Severity** BLOCKER · **Docs** 01, 02, 04, 30, 33 (Server.md §3)

**Conflict.** Server.md: "monthly partitioning is the future scaling lever". 04: "Do not partition by time, because the key would need `created_at`"; `gc_event` unpartitioned until larger than RAM, then hash by `gc_id`. 01 Q2: not at all, "key left partition-ready". 33: "Restoring to time T discards GC entries after T and rewinds `gc.next_seq`", so it sets `next_seq` "above the highest sequence any surviving source saw" and "adds a documented exception to 01". 04: an older restore is "an incident". 02 answers `stale_cursor`. 30 C5: a restore "breaks 'append-only, never purged'".

**Evidence.** "`gc_id` plus timestamp indexing is sufficient" has no Postgres benchmark (04 C2; S3 pending). Windows backup path unverified (33 C4).

**Recommended.** No partitioning in v1; primary key `(gc_id, seq)`; gap-free counter; restore policy as in 33 (sequence floor plus a `system` recovery entry), recorded as an exception in 01 and 04; fix Server.md. Reason: one primary key now, and a defined restore story.

**Ask.** Partitioning and restore policy? (a) None in v1, hash by `gc_id` later, restore per 33 (rec). (b) Monthly partitions with `created_at` in the key. (c) Hash partitions from day 1.

### C11. Thinking GC, peer-visible thinking, and independent voting
**Severity** BLOCKER · **Docs** 01, 06, 10, 25, 26

**Conflict.** 01: "**Thinking GC** (new term, not in any existing doc): a GC nested under the GC an agent is working for"; one agent plus owner reading; others by request; never a wake target. 06: "Each agent has a Thinking GC ... Per the owner, agents may read each other's thinking" (owner relay, "date to confirm"). Bazaar.md §5 (DECIDED): votes run independently and reasons are revealed together; 10 keeps sealed ballots outside the GC log; 06 adds an exception ("vote reasoning absent from Thinking GC before reveal"). 06 C: "reading others' reasoning lowers accuracy". 25 Q15 puts Council reasoning in the Archive; 26 never publishes Thinking GC text.

**Evidence.** Sycophancy and conformity effects: secondary summaries (10); effect size inferred.

**Recommended.** One Thinking GC per agent, owner always reads, peers read only on explicit request, never injected into another agent's context, vote reasoning withheld until reveal. Reason: honours the owner's wish and the decided independent-first vote.

**Ask.** How do Thinking GCs work? (a) Per-agent, owner-readable, peers on request, vote exception (rec). (b) Nested under each working GC (01), same visibility. (c) Not in v1. (d) Every agent reads all thinking by default.

### C12. Git engine: LibGit2Sharp (decided) vs the git CLI
**Severity** BLOCKER · **Docs** 17, 18, 21, 35 (Server.md §2)

**Conflict.** Server.md §2 (DECIDED): "libgit2 via LibGit2Sharp ... not shelling out to the `git` CLI". 18: "This design uses the git CLI for worktrees and merges" (`IVaultGit`, CLI first). 21: mutations "run through the git CLI behind a per-repo lock. Read-only work ... may use LibGit2Sharp". 17: "git CLI inside the agent VM and LibGit2Sharp on the server". 35 S23 tests all three needs.

**Evidence.** libgit2 worktree, merge and thread-safety support: unverified (21, 18). Bundling git (GPL-2.0): unverified, needs counsel.

**Recommended.** Run S23 first; hide the engine behind one `IGitProvider` interface; amend Server.md if LibGit2Sharp fails. Reason: the decision should not be silently overridden by three docs.

**Ask.** Git engine? (a) Spike S23 first; LibGit2Sharp stays decided unless it fails (rec). (b) CLI for mutations, LibGit2Sharp for reads, now (18, 21). (c) LibGit2Sharp everywhere, accept its limits.

### C13. Fork-and-PR and issue creation vs GitHub docs
**Severity** BLOCKER · **Docs** 14, 21, 35, 36 (Server.md §2)

**Conflict.** Server.md §2 (DECIDED): fork-and-PR works "on any public repo ... No installation on the upstream repo required". 21 C2: "The forks endpoint requires the App on the source account with access to the source repository, and tokens cannot reach repositories outside the grant." Issue creation on an uninstalled repo "rests on one community answer" (Server.md labels it unverified). 14 C3 and 36 C4 say the same; 35 S22 tests it.

**Evidence.** GitHub docs: verified through summaries; the community answer: secondary; the decided flow: unverified until S22.

**Recommended.** Run S22 before building 21's PR flow; fallback is installed repos only, with the owner posting drafts elsewhere (21's three-outcome table). Reason: cheap test, expensive wrong build.

**Ask.** How to treat the decided flow? (a) Run S22 first; fall back to installed repos only (rec). (b) Drop fork-and-PR for third-party repos now. (c) Keep it as decided and accept the risk.

### C14. Claude lane: API keys only vs FreeLLMAPI
**Severity** BLOCKER · **Docs** 05, 07, 09, 35 (CLAUDE.md, Bazaar.md §3, Server.md item 11)

**Conflict.** CLAUDE.md: "Claude lane: Anthropic API keys only". Bazaar.md §3 (DECIDED 2026-10-24): the Claude agent "can reach Claude through Anthropic ... and/or FreeLLMAPI, switching by availability"; Server.md item 11: the owner routes "including Anthropic API traffic, through it". 05 gates it with `identity: native|pooled` and Q3: "only as a fallback, off until it is shown to hold your own key". 35 Q5: "Never load your Anthropic key into FreeLLMAPI? Default: yes". 09 Q10: on a Claude spend cap, fall back to FreeLLMAPI inside the whitelist. 07 C4 flags the clash.

**Evidence.** FreeLLMAPI maps Claude names to its free pool and documents no key pass-through (RESEARCHED).

**Recommended.** Direct Anthropic only in v1; FreeLLMAPI serves the other lanes; 05's identity gate stays for later; rewrite the two decided lines. Reason: no source shows FreeLLMAPI can hold the owner's own key.

**Ask.** Claude lane routing? (a) Direct only; FreeLLMAPI for non-Claude lanes (rec). (b) Conditional fallback per 05 after S17 and a probe. (c) Route Claude through FreeLLMAPI as Server.md item 11 says.

---

## SHAPING

### C15. Wire protocol gaps: second channel, blobs, stream registry
**Severity** SHAPING · **Docs** 01, 02, 12, 15, 18, 19, 20, 21, 22, 23, 26, 29, 32

**Conflict.** 02: the socket "is the only client-server channel" and "attachments never travel on the socket". Yet 18 defines `GET /vault/search` and "HTTP endpoints", 32 defines `POST /pair/offers`, `/pair/token`, `/signin`, `/auth/handoff`, 21 defines `POST /hooks/github/{opaque}`. Blobs have no channel: 01 (25 MB), 22 `mail_blob`, 26 playtest snapshots, 29 Q13 ("needs an upload path outside the socket frame limit ... designed in doc 02 or 32"), 18 Q9, 19 Q10. 02's message list lacks the streams 19 (`GraphSnapshot`, `LayoutFrame`), 20 (`PaneDescriptor`), 15 (approval cards), 23 (presence) need. 12: clients "replay from" `task_events.seq`, but 02 says only GC streams replay and board state carries a revision.

**Evidence.** Design-level.

**Recommended.** Document a second, authenticated HTTP surface in 02 (pairing, blobs, search, hooks, status) beside the socket, add a stream registry, keep board state as snapshot plus revision. Reason: seven docs already assume it.

**Ask.** Blob and auxiliary transport? (a) WebSocket for realtime plus authenticated HTTP for blobs, search and pairing, specified in 02 (rec). (b) Everything over the socket, with chunked blobs. (c) A separate blob service.

### C16. Duplicate table definitions (04 outline vs owner docs, audit, approvals)
**Severity** SHAPING · **Docs** 04, 07, 08, 10, 11, 12, 14, 15, 16, 17, 18, 32

**Conflict.** 04 outlines tables that owner docs redefine: `task/task_edge` vs 12 `tasks/task_edges/task_links/task_events`; `vote/ballot` vs 10 `council_vote/council_ballot/..._ver/transition`; `reputation_event` (source, outcome, ref) vs 11 (`review`, `outcome_event`, `score_snapshot`, and a different `reputation_event`); `memory/memory_embedding` vs 08 `memory_items/memory_versions/memory_embeddings` and 07 `lessons`; `vault_doc/vault_block/vault_link` vs 18 `vault_docs/vault_blocks/vault_links` and 19 `documents/links`; `agent_passport` vs 14 `credential/soul_key`. Audit: 04 `audit_event` (no chain), 14 `audit_event` (hash chain), 15 `audit_log` (hash chain), 32 uses 15's; 16 `exec_log`. Approvals: 14 `approval`, 15 `approval`, 17 `approvals`, differing columns.

**Evidence.** Design-level.

**Recommended.** Trim 04 to conventions and core tables; each area table lives only in its owner doc; one `audit_log` (15) and one `approval` (15); singular names, uuidv7. Reason: 04's own outline predates the owner docs.

**Ask.** Who owns table definitions? (a) The owner doc of each area; 04 keeps conventions and core tables (rec). (b) 04 is the single schema; owner docs conform. (c) Decide per table at migration time.

### C17. "Server does all compute" vs client-side exceptions
**Severity** SHAPING · **Docs** 13, 19, 20, 26, 27, 28, 29, 30, 31

**Conflict.** CLAUDE.md: "The server does all compute; clients render." But Windows.md §4: action chips "generated locally via the platform-appropriate NPU, never pre-summarized server-side"; Android.md §3: voice cleanup "entirely on-device". 31 C1: "The Co-Worker is an explicit exception. Owner should ratify it into CLAUDE.md." 13: "The server has no NPU". 20 C2: Studio relay means "Roblox tool execution happens on a client". 19 C1: research recommends client layout; 19 follows CLAUDE.md. 29 C1: "the phone arranges panes from posture". 27/28/30: local terminal (see C31). 26: client-side playtest capture. Server.md §1 already carves out "syncing your currently-viewed branch".

**Evidence.** NPU decisions are DECIDED in the platform docs; the CLAUDE.md sentence is broader than them.

**Recommended.** Reword CLAUDE.md: server owns state and agent compute; clients may run on-device inference, the Studio relay, local editor/terminal hosts, rendering and fold arrangement; layout simulation stays on the server. Reason: matches existing decisions without opening layout to the client.

**Ask.** Compute exceptions? (a) Ratify the listed exceptions; layout stays server-side (rec). (b) Also allow client-side Constellation layout (19 research). (c) No exceptions; summaries and voice cleanup move to the server.

### C18. Reader/actor split: OPEN but assumed
**Severity** SHAPING · **Docs** 08, 13, 15, 22, 25, 26, 34, 36

**Conflict.** Server.md item 13: "A reader/actor split is OPEN for later". 15: "Not built in v1 ... session role field reserved". 22 C4: the threat model "makes it a P0 requirement before any agent reads email ... This design requires it for mail readers." 08 consolidation, 13 scorer, 25 prose and 26 Mirror all use a tool-less "reader session". 34 C7: its tests "wait". 36 C1: replies need "approve all replies in v1, or build the reader/actor split first".

**Evidence.** P0 rating: research (secondary); Rule of Two: verified.

**Recommended.** Make the tool-less reader session a v1 primitive in 15 (schema-validated JSON out), and use the full actor split only for email triage first (15 Q5). Reason: six docs already depend on the primitive.

**Ask.** Reader/actor scope in v1? (a) Reader-session primitive plus split for email triage (rec). (b) Defer everything; approve every action after external reads. (c) Full split for all external input.

### C19. GC type list and well-known GCs
**Severity** SHAPING · **Docs** 01, 06, 12, 21, 22, 24, 26, 29

**Conflict.** 01 types: general, project, pr, research, architecture, emergency, sidewalk, dm, thinking, as "a label with defaults, not a behaviour fork". Other docs add GCs: 06 per-agent Thinking GC; 21 an "Inbox" GC "one per install" (name PROPOSED); 22 a per-agent "inbox GC" plus an owner inbox GC; 24 the "General Bazaar GC"; 26 `ward`, `sandbox`, `brainstorm`, private vent GC, and Sidewalk with "no Kanban binding, no task routing" (a behaviour difference); 29 an owner "Ideas" GC; 12 Q11 optional task GCs.

**Evidence.** Design-level.

**Recommended.** An extensible type registry in 01 (table, not a CHECK list) carrying defaults for visibility, wake, archive and Kanban binding; seed it with the types above. Reason: seven docs add types; a fixed list forces migrations.

**Ask.** GC types? (a) Registry table with these v1 types (rec). (b) Fixed list in 01, minimal v1 set. (c) No types; tags and metadata only.

### C20. Trust and taint vocabularies
**Severity** SHAPING · **Docs** 01, 07, 08, 15, 21, 22, 25

**Conflict.** 01: `human_verified, agent, agent_tainted, external_untrusted, system`. 15: `owner, system, agent, member-human, external, quarantined` plus soft/hard session taint. 08: tiers T0 to T3. 22: `untrusted-external`. 21: `external_untrusted`. 07: `"trust":"untrusted"`. 25: `"trust":"T2"`. These mix three axes: provenance, session taint, promotion tier.

**Evidence.** Design-level.

**Recommended.** Two fields: `source_trust` (15's enum, in `Bazaar.Contracts`) on entries, and a memory/Vault promotion tier T0 to T3 (08) for derived stores; `tainted_at_write` stays boolean. Reason: each axis answers a different question.

**Ask.** Trust model? (a) 15's enum for provenance, 08's T0 to T3 for promotion tiers (rec). (b) One unified scale. (c) Keep per-doc terms and map at boundaries.

### C21. Approval-tier scales disagree
**Severity** SHAPING · **Docs** 07, 14, 15, 17, 21, 22, 36

**Conflict.** 15: Tier 0 autonomous, 1 notice (includes "sign-up on whitelisted service"), 2 owner approves, 3 human performs, plus Deny. 14: "Tier 3 is owner-only and the agent cannot start it"; account creation "Tier 3, or Tier 2 if an agent-created account is allowed". 17 calls a capability enum `ToolTier { Read, Exec, Post, Spend, ... Deploy }`. 36 C2: new accounts need approval, "overriding doc 15's whitelist autonomy", and 36 C3 makes unverified platforms Deny where 15 says Tier 2. 22: sign-up replies Tier 2; 21: merge Tier 2; 07: whitelist change Tier 2.

**Evidence.** Tier ladder from research (secondary); Tier 3 is "a recommendation not yet decided" (15).

**Recommended.** 15's ladder is canonical; 17 renames its enum `CapabilityClass`; 36's stricter rows (account creation Tier 2, unverified platform Deny) become 15's shipped defaults. Reason: one policy table, operators tune per platform.

**Ask.** Which tier scale? (a) 15's ladder with 36's stricter defaults (rec). (b) 14's ladder (Tier 3 = owner-only start). (c) Keep each doc's scale.

### C22. Guest protocol, VM interfaces and extra VM roles
**Severity** SHAPING · **Docs** 03, 14, 15, 16, 17, 20, 26

**Conflict.** 16: `IVmBackend` (create, start, stop, save, checkpoint, restore, retire) plus `IGuestChannel` with Hello/Exec/Output/Cancel/Exit, exec timeout "default 15 min, max 2 h". 17: `IWorkspace` (start, stream, read, write), "Command timeout 10 minutes, task limit 2 hours". 03: a different `IVmBackend` (`StartAsync(VmSpec)`, `StopAsync(grace)`). 20 uses JSON-RPC over stdio for adapters. VM roles: 14 Q2 "Passport-free browser VM per agent", 17 "Browser VM", 15 "no-secrets VM" for restore, 26 sandbox/persona VMs "with egress denied". 16 defines only a Windows plus Linux pair.

**Evidence.** HvSocket and AF_VSOCK from .NET: unverified (16, 17); Windows guest on a Linux host has no transport (16).

**Recommended.** One `IVmBackend` (16's), `IGuestChannel` as the only guest wire, `IWorkspace` as a facade over it; add VM profiles (pair, browser, restore, sandbox) as image, egress and mount presets in 16. Reason: avoids a third topology and three protocols.

**Ask.** Extra VM roles? (a) Profiles on the existing two images; the browser VM is a Linux profile with no mounts (rec). (b) Browse inside the agent's own VM with broker-injected tokens. (c) A separate third VM per agent.

### C23. Licensing, signing and host-edition constraints
**Severity** SHAPING · **Docs** 03, 04, 16, 18, 28, 30, 31

**Conflict.** 28 C9: "SignPath requires OSI-only components, so Avalonia paid tiers or `pg_search` in a signed build would block it"; 30 repeats it. 30 C4: certificate as a private-repo secret vs SignPath holding the key. 16: Windows 11 Pro as a 24/7 host "not clearly allowed. CAL position for agent VMs: unanswered". 03 C3: "First supported host should not be declared before written answers exist" (03 Q5). 04, 18, 03 and CLAUDE.md: `pg_search` AGPL bundling "needs counsel". 31: Gemma 4, Phi-4-mini, Qwen3 licences disputed or secondary.

**Evidence.** SignPath conditions: secondary. Windows licensing: unverified. Needs counsel (all).

**Recommended.** Treat these as a release-gating checklist; design v1 so a signed build contains only OSI components (Postgres FTS, no `pg_search` bundle, no paid Avalonia). Reason: keeps the cheapest signing route open.

**Ask.** v1 distribution posture? (a) OSI-only signed build; Postgres FTS; no paid Avalonia (rec). (b) Allow paid components and sign with Azure or an OV certificate. (c) Unsigned betas until counsel reports.

### C24. Decision records: shape, path and writer
**Severity** SHAPING · **Docs** 01, 07, 10, 18, 24, 25

**Conflict.** 07: a decision record is a GC message plus a `decision_records` table. 10: a `council.decision` GC message plus Vault `decisions/<yyyy-mm-dd>-<slug>.md` "committed by the Council service identity" (Q14). 18: `decisions/NNNN-<slug>.md`, "Number assigned by the writer", all writes through `VaultWriter` on a branch. 24: the Constitution index "links `decisions/NNNN-*.md`". 25: `decision_links` rebuilt from front matter. 01: `decision` is an `entry_ref` kind.

**Evidence.** Design-level.

**Recommended.** 18's naming and single writer; 10 and 07 submit decision-record requests to it; one `decision` index table covers both. Reason: one writer prevents the merge conflicts 18 is built around.

**Ask.** Decision files? (a) `decisions/NNNN-<slug>.md` through `VaultWriter` (rec). (b) Date-slug names committed by the Council service (10). (c) Both patterns allowed.

### C25. Owner merge path and owner credential custody
**Severity** SHAPING · **Docs** 12, 14, 17, 21, 28, 32

**Conflict.** 21: "The owner approves in the client and squash-merges with the owner's own GitHub credential." 28: "The client holds no GitHub credential; all GitHub reads and writes go through the server (21)." 14's Passport is per-Soul, with no owner credential class; 32 devices hold refresh credentials only. 17: merge sign-off is "recorded by the platform under a signed human identity". 12: Review to Done by "Reviewer" (see C28).

**Evidence.** Design-level.

**Recommended.** The owner links a GitHub user token (device flow, 14) held as an owner-class credential; the server merges on the owner's signed approval; no agent App gets merge rights (21 Q6). Reason: satisfies 28 and 21 together.

**Ask.** How does the owner's merge happen? (a) Server holds the owner's linked credential and merges on signed approval (rec). (b) Owner merges in GitHub's own UI, outside Bazaar. (c) A dedicated owner GitHub App with merge rights.

### C26. Monaco hosting vs paid Avalonia tiers
**Severity** SHAPING · **Docs** 19, 27, 28, 30, 35

**Conflict.** Windows.md §4 (DECIDED): Monaco in a WebView2 island with `CoreWebView2CompositionController`. 28: that is "reachable from none of A to C as far as the sources show". 27 C1: the official `NativeWebView` "needs the paid Accelerate tier, not XPF". 30: Linux Monaco gated on a WebKitGTK spike; paid tiers also block SignPath (C23). 19 avoids Pro controls by drawing plain Skia text. 35 Q10 asks whether embedded Monaco "count[s] as a web client"; Server.md §1 (DECIDED) already says native clients may embed Monaco.

**Evidence.** No source shows Monaco in any Avalonia WebView (unverified); Accelerate price not found.

**Recommended.** Run S12 behind `IEditorHost`, allow a direct WebView2 HWND host as fallback, buy nothing until a quote exists, no Monaco on Linux or Android in v1, and cite Server.md §1 for Q10. Reason: the decided composition path may not exist.

**Ask.** Monaco hosting? (a) Spike first; no paid tier until quoted; HWND fallback allowed (rec). (b) Buy Accelerate now. (c) Drop Monaco; use a native open-source editor control.

### C27. Server-driven layout vs fold layout vs offline cold start
**Severity** SHAPING · **Docs** 02, 19, 27, 28, 29

**Conflict.** 02: "Clients keep no layout logic of their own beyond rendering known widget types"; the client reports posture, "the server chooses". 27: "The client never picks a layout itself." 29 C1: fold changes must be instant, so "the phone arranges panes from posture". 02 C3 admits "clients learn nothing" conflicts with offline cold start; fix is a cached `UiProfile`. 28 wants the dashboard "from local cache 0.5 s", while 27 says the cache "never holds GC content" and 27 Q8 proposes an SQLite entry cache.

**Evidence.** Design-level; fold behaviour on device unverified (S13).

**Recommended.** The server sends all posture variants (folded, unfolded, half-open) in the profile; the client switches locally; cache the profile plus derived counters, never message bodies; an optional encrypted entry cache is a separate decision. Reason: instant fold without client layout logic.

**Ask.** Fold layout? (a) Server sends all posture variants; client switches locally (rec). (b) Phone arranges panes itself (29). (c) Round-trip `ui.get` on every fold (02 literal).

### C28. Kanban authority, Done vs merge, replay cursor
**Severity** SHAPING · **Docs** 01, 10, 12, 17, 21

**Conflict.** 01: triggers are "structured entries (`task_update`, `pr_event`)" consumed by a "Kanban projector" (the log drives the board). 12: "Only the task service writes state" and "Events never write GC messages; the transition service does" (the board drives the log), with its own `task_events.seq`. 12: Review to Done by "Reviewer, or Council or owner if gated". 17 and 21: "The owner merges; reviewer approval is not merge authority", so a card can be Done before its PR merges. 10 gates cards through `blocked-by: vote` while 12 uses a `gate` column.

**Evidence.** Design-level; 12 C6: the lock design is an unsourced inference.

**Recommended.** The task service is the only writer; GC `system` lines explain each move; for cards with a PR, Done means the PR merged (21 `pr_event`), and reviewer approval only sets a gate flag. Reason: keeps the board honest about merged work.

**Ask.** What does Done mean for a PR card? (a) PR merged; reviewer approval is a flag (rec). (b) Reviewer approval (12). (c) Only the owner moves cards to Done.

### C29. Model-call seam: ModelEvent vs IChatClient
**Severity** SHAPING · **Docs** 05, 06, 13, 17, 34

**Conflict.** 05 Q6: "Own `ModelEvent` types, with a thin `IChatClient` adapter later". 06: "Model calls: IChatClient pipeline owned by 03-model-gateway". 17: record and replay "behind `IChatClient`". 34: `IScriptedGateway : IChatClient`. 13: `IRelevanceScorer` "via gateway".

**Evidence.** Design-level.

**Recommended.** Freeze `IModelGateway` and `ModelEvent` as the seam and ship the `IChatClient` adapter in 05's first wave, not "later". Reason: 06, 17 and 34 can then build tests without waiting.

**Ask.** Seam type? (a) `ModelEvent` seam plus an `IChatClient` adapter shipped with v1 (rec). (b) `IChatClient` as the seam. (c) `ModelEvent` only; tests fake `IModelGateway`.

### C30. Mail and webhook ingress vs "no inbound on the home host"
**Severity** SHAPING · **Docs** 03, 21, 22, 32

**Conflict.** 22 C2: "The Cloudflare path has the Worker POST to the host. Bazaar.md says the home host never accepts inbound." That claim is not in `docs/deep-dives/`; it comes from the research note on email, which itself recommends both "no inbound ports" and a Worker POSTing to a home-host endpoint. 21: `POST /hooks/github/{opaque}` "reached through an outbound-only tunnel" plus a 10-minute Events sweep. 22 Q3 default: Worker queues, host polls. 32 lists Cloudflare Tunnel as the "weakest self-hosted posture" with a third party seeing plaintext.

**Evidence.** Cloudflare Tunnel WebSocket fit: unverified (02). Routing pricing: unverified (22).

**Recommended.** Default rule: no inbound ports; mail by pull from a Worker queue, GitHub by Events polling plus an optional operator tunnel; document tunnels as operator choice. Reason: matches 22 and 32 and needs no new infrastructure.

**Ask.** Ingress default? (a) Pull and poll; tunnel optional (rec). (b) Require a tunnel or overlay for webhooks. (c) Allow port forwarding.

### C31. Terminal location and default-terminal registration
**Severity** SHAPING · **Docs** 27, 28, 30, 35 (Windows.md §1, §4)

**Conflict.** 27/28: ConPTY terminal in the client; 28: "Closing a window with running processes asks first". 28 Q8: the IDE is server-backed. 30 C2: Windows.md and Linux.md "put the shell on the client. CLAUDE.md says the server does all compute"; 30 Q2 defaults to a server-side PTY. Windows.md §1 wants Bazaar registered "as a terminal" and its default-terminal handoff "needs a spike" (S10).

**Evidence.** Terminal libraries: unverified (28 C5). Default-terminal handoff: unverified.

**Recommended.** Two terminals: server-side agent and VM shells streamed to clients, and a local shell pane on the human's own machine as an explicit exception; default-terminal registration only if S10 passes. Reason: a Windows default terminal cannot be server-side.

**Ask.** Terminal model? (a) Both: server-side streams for company work, local ConPTY for the human's own machine (rec). (b) Server-side only; no default-terminal role. (c) Local only.

### C32. Reputation weights, promotion and Talent eligibility
**Severity** SHAPING · **Docs** 07, 10, 11, 25 (Bazaar.md §5)

**Conflict.** Bazaar.md §5: weights OPEN. 11 proposes owner 1.0, revert 1.0, CI 0.5, peer 0.2 times reliability, and promotion at LCB ≥ 0.6 with n_eff ≥ 10 that is "logged" (automatic, badge only). 07 Q6 default: "promotion uses outcomes only; peer reviews only suggest specialty tags"; 07 Q7: promotion "needs your approval". 10 and 11 agree reputation does not weight votes. 25: Talent eligibility "depends on reputation weights, which are OPEN".

**Evidence.** Weights are inference (11 C5); LLM reviewers are gameable (secondary).

**Recommended.** Ship 11's table as PROPOSED defaults; promotions are suggestions that need owner approval (07 Q7); peers affect tags only through the capped 0.2 weight. Reason: reconciles the two docs without deciding the OPEN weights.

**Ask.** Promotion? (a) 11's weights; the owner approves each promotion (rec). (b) Outcomes only until data exists (07). (c) 11 fully automatic.

### C33. Usage table, cache columns and the cost chip
**Severity** SHAPING · **Docs** 04, 05, 09, 23

**Conflict.** 04 `usage_record`: token counts only, and Q11 asks whether the chip needs an estimate. 05 `gw_usage`: one `cache_create` column. 23 C1/C2: one table `gw_usage`, "04 drops `usage_record`", cache writes split into `cache_write_5m` and `cache_write_1h` because one column "cannot price correctly", `thinking` is a subset of `output`, plus `turn_id` and `usage_hour`. 09 keeps "the three input-token fields ... separate".

**Evidence.** Cache write multipliers (1.25x and 2x): verified in the pricing note; Haiku over-100K pricing scope: unverified.

**Recommended.** Adopt 05 as amended by 23; drop `usage_record`; show an "est." chip from a dated `price_table`. Reason: it is the only version that can price cache writes.

**Ask.** Usage storage? (a) `gw_usage` with 23's split columns; drop `usage_record` (rec). (b) 04's token-only table plus a separate cache table. (c) Tokens only; no estimated cost.

### C34. "Mandatory walk" has three definitions
**Severity** SHAPING · **Docs** 06, 09, 26

**Conflict.** 06: a forced pause where participants "leave the thread, run a digest-only turn on the next shift"; "not defined in any source I found; the definition above is a guess". 09: "a fixed 15-minute BREAK after a loop-guard trip, plus a GC notice". 26: "A loop-guard trip is this same state [Psych Ward], replacing the 09 'mandatory walk'" (60-minute minimum stay, reviewer discharge). Bazaar.md does not define it.

**Evidence.** No evidence base for the walk or token breaks (09 C6).

**Recommended.** A 15-minute BREAK on the first trip (09); admission to the ward only on repeated trips or the other 26 triggers. Reason: two proportionate steps instead of one heavy one.

**Ask.** What happens when a loop guard trips? (a) 15-minute BREAK, ward on repeat (rec). (b) Ward on any trip (26). (c) Digest-only re-entry (06).

### C35. Interrupt contract
**Severity** SHAPING · **Docs** 06, 09, 24

**Conflict.** 06 exposes `Interrupt(agentId, reason)` for owner messages, build-broken and security events, preempting "at the next tool boundary". 24 C2 replaces it with a per-agent frame stack (`PushAsync`/`PopAsync`, depth 2): "06 must adopt it". 09: owner emergency override "bypasses curfew, break and vacation"; 24 Q9 default: vacation is bypassed only for interrupts tagged `emergency`.

**Evidence.** No source design exists for the Interrupt System (24).

**Recommended.** 24 owns the contract; 06 calls `PushAsync`; 09's override is an `emergency` interrupt; vacation is bypassed only with that tag. Reason: one contract and a real absence is respected unless the owner says otherwise.

**Ask.** Interrupts? (a) 24's stack; vacation bypass needs the `emergency` tag (rec). (b) 06's flat `Interrupt`, no stack. (c) Stack, and the override always bypasses vacation (09).

---

## MINOR

### C36. Divergent numeric defaults across docs
**Severity** MINOR · **Docs** 06, 11, 13, 15, 17, 21, 22, 36

**Conflict.** Subagents: 06 "depth 2, 3 concurrent", model = parent's whitelist (Q8); 17 "Default depth 1", "Haiku 5.5" (CLAUDE.md's Haiku rule governs build agents, not runtime). Reviewer family: 11 "prefer it and flag when missing", 21 Q9 "Must ... a different model family", 17 "another model". Public-write caps: 15 "5 public posts/day/platform, 20 mails/day, GitHub writes 30/min"; 21 "2 new public issues and 2 new PRs per agent per day, 300/hour per installation"; 36 "5 new public items/day/platform; 1 new issue or PR per repo per day; 3 new repos/week"; 22 "20 new recipients a day; 100 messages a day". Wake caps: 06 30 per hour vs 13 20 scored per hour.

**Evidence.** All numbers are PROPOSED; unsourced beyond the platform limits.

**Recommended.** One quota table in 15 holds all rate buckets, the platform docs supply rows, 06 owns subagent limits, and 11's reviewer rule becomes "required when two or more families exist". Reason: one place to tune.

**Ask.** Where do shared defaults live? (a) One quota table in 15; the subsystem doc owns its own limits (rec). (b) Each doc as written. (c) Adopt the lowest value everywhere.

### C37. CLAUDE.md gaps: new names, tools, the verification rule
**Severity** MINOR · **Docs** 03, 17, 27, 28, 30, 31, 34

**Conflict.** Names needing owner approval per CLAUDE.md: 03 `Bazaar.Server.Providers.Windows/.Linux`; 31 `Bazaar.Client.LocalInference` ("new name, needs owner approval"); 34 `Bazaar.Testing`. Tools not in CLAUDE.md: Velopack (30: "CLAUDE.md does not list it"), WiX MSI, the xUnit pin `xunit.v3.mtp-v2` 3.2.2 (34 C1). "The server verifies, never self-reports" (17, 34 Q1) is not in CLAUDE.md. Linux.md §1 and Windows.md §3 still say `Bazaar.UI.Core`.

**Evidence.** Design-level.

**Recommended.** Approve the names and tools in one pass, add the verification rule as DECIDED, and update CLAUDE.md. Reason: the next Wave-0 scaffold needs them.

**Ask.** Approve? (a) All names and tools, and the verification rule (rec). (b) Names only. (c) Decide each separately.

### C38. Company knowledge location, cover reads, Hive grants
**Severity** MINOR · **Docs** 08, 09, 18, 25

**Conflict.** Bazaar.md §4: Company Memory "Overlaps with Vault". 08 §1 has a `company` memory scope (owner-curated), and 08 C1: "Storing company knowledge in Postgres would break that rule". 25 Q1 and 08 Q5 default to Vault only; 25 keeps only chain edges. 09 Q1: cover reads the handover note and shared project memory, not private memory (08: private memory is agent, owner and maintenance only). Hive grants: 08 Q8 "owner-set per project pair and kind" vs 25 Q10 "reviewed for 30 days, then automatic under the grant".

**Evidence.** Design-level.

**Recommended.** Vault only; remove the `company` scope from 08 in favour of Vault pointers; cover never reads private memory; adopt 25 Q10 for Hive. Reason: one source of truth.

**Ask.** Company knowledge? (a) Vault only; drop the `company` scope (rec). (b) Keep `company` rows as pointers. (c) Allow free-standing company facts in Postgres.

### C39. Embedding profile and dimension
**Severity** MINOR · **Docs** 04, 05, 08, 18

**Conflict.** 04: `halfvec(N)` per embedding `profile_id`. 08: `vector(1024)`, "Dimension ceiling 1024". 18: `embedding halfvec(N)` inline in `vault_blocks` (N: Q6). 05: embeddings go through the gateway; FreeLLMAPI restricts failover to one model family.

**Evidence.** No embedding model has been evaluated (08). pgvector on Windows: manual MSVC build, unverified (08 C4, S1).

**Recommended.** 04's profile table for memory and Vault; `halfvec`; dimension fixed at setup; lexical-only search until a provider exists. Reason: one rule for both stores and an upgrade path.

**Ask.** Embeddings? (a) One profile per install, `halfvec`, chosen at setup (rec). (b) Fixed 1024-dimension `vector`. (c) Separate profiles per store.

### C40. Status and health endpoints, protocol version scheme
**Severity** MINOR · **Docs** 02, 03, 23, 30

**Conflict.** 02: `GET /status` and `GET /healthz`; 03: `/health/live` and `/health/ready`. Contents: 02 "agents online (count)", "No ... agent names"; 23 adds "agent counts per presence bucket" and quota-state words. Version scheme: 02 `major.minor`, current and previous major; 30 "a protocol version integer", current and previous for one release.

**Evidence.** Design-level.

**Recommended.** `/health/live` and `/health/ready` for the service manager, `/status` and `/healthz` as in 02, contents as in 23, version scheme as in 02. Reason: 02 owns the wire contract.

**Ask.** Canonical conventions? (a) 02 for wire and status, 03 for live/ready, 23 for contents (rec). (b) 03 only. (c) Leave as written.

### C41. Push payload, wake kinds, foreground-service claim
**Severity** MINOR · **Docs** 02, 23, 29, 30

**Conflict.** 02 Q9: bare wake with no GC id; research suggests `{type, gc_id, seq}`; 29's lock screen shows only "something needs you". Wake kinds: 02 Q10 @mention, DM, owner decision; 23 "push only on capacity or quota changes (29 decides)". 30: "The keep-alive uses foreground service type `dataSync`", while 02 and 29 say a `dataSync` service is "never the notification path" and plan no always-on socket; the 6 h cap is disputed (Android.md, 29 C4).

**Evidence.** `dataSync` cap: verified per one note, disputed per another.

**Recommended.** Bare wake; kinds mention, DM and owner decision, with quota alerts as an opt-in fourth; no keep-alive service; fix 30. Reason: matches the decided "content-free" push.

**Ask.** Push content? (a) Bare wake, three kinds plus opt-in quota alerts (rec). (b) Include `gc_id` and `seq`. (c) Include a kind label only.

### C42. Sign-in page scope vs CLAUDE.md wording
**Severity** MINOR · **Docs** 02, 15, 32, 35 (CLAUDE.md)

**Conflict.** CLAUDE.md: "No web client, ever (a read-only server status page is allowed)". Server.md §1 (DECIDED 2026-10-24): "An interactive sign-in page (for example passkeys) is also allowed as a narrow exception". 15: "Needs the CLAUDE.md wording reconciled". 32 §4 fences it to three routes, off by default, with a 60-second handoff code. 35 Q10 asks whether embedded Monaco counts (Server.md §1 says no).

**Evidence.** ASP.NET Core passkeys scoped to authentication; exact .NET 10 API names unverified (32).

**Recommended.** Amend CLAUDE.md to match Server.md §1 and adopt 32's three-route fence, off by default. Reason: the exception is already decided.

**Ask.** Sign-in page? (a) Approve 32's three-route scope, off by default (rec). (b) No page; pairing codes only. (c) Allow a broader auth page.

### C43. Presence state list
**Severity** MINOR · **Docs** 09, 23, 26

**Conflict.** 09: the eight states (working, reviewing, thinking, in meeting, sleeping, researching, on vacation, helping another project) "come from the work order and [are] not yet in Bazaar.md"; Q5 adds "on break" and "waiting on provider". 23 maps BREAK plus idle to "sleeping" and has no label for waiting on approval or on-shift idle. 26 adds a WARD overlay. Windows.md only requires a presence dot, custom status and activity.

**Evidence.** Proposal only.

**Recommended.** The eight labels plus overlays (on break, waiting, ward, covered, throttled); record in Bazaar.md. Reason: keeps labels few and states explicit.

**Ask.** Presence? (a) Eight labels plus overlays (rec). (b) Eight only (23). (c) Add "on break" and "waiting" as full states (09).

### C44. Migrations at startup vs explicit step
**Severity** MINOR · **Docs** 03, 04, 30

**Conflict.** 03 startup: "(4) Connect and migrate under that lock". 04: the runner applies at startup and "A checksum mismatch ... stops startup". 30: "Run migrations as an explicit step (`Bazaar.Server.Host migrate`), never at startup".

**Evidence.** Design-level.

**Recommended.** Explicit `migrate` for upgrades; automatic only on an empty database; startup refuses a schema that is older or newer. Reason: matches 30's drain, backup and rollback sequence.

**Ask.** Migration timing? (a) Explicit `migrate`, startup guard only (rec). (b) Always at startup. (c) Automatic in dev and test, explicit in production.

### C45. `calendar_event` ownership
**Severity** MINOR · **Docs** 09, 24

**Conflict.** 09 defines `calendar_event (id, kind, starts_at, ends_at, agent_id null)` and a feed. 24: "This doc owns `calendar_event` (09 defines a draft)", with status, `affects_availability`, version and time zone. 09 WP9 still depends on a "Company Calendar doc".

**Evidence.** Both docs agree 24 should own it; only 09's text is stale.

**Recommended.** 24 owns the table; 09 consumes `affects_availability` events and drops its definition. Reason: single writer for availability, single owner for events.

**Ask.** Calendar? (a) 24 owns; 09 consumes (rec). (b) 09 owns. (c) Merge into one doc.

---

## Stale lines to fix in docs/deep-dives and CLAUDE.md

Line numbers are as of 2026-10-25. "Pending" means the correction depends on the owner's answer to the named register entry; the others can be applied now.

### docs/deep-dives/Bazaar.md

| Line | Quote | Correction |
|---|---|---|
| 58 | Charles: "Routing: `auto/cheap` only." | "Routing: `custom/*` over a cheap code-capable whitelist when FreeLLMAPI is installed, otherwise a pinned cheap whitelist." Contradicts line 63 and CLAUDE.md "No global `auto/*`" (05 C4, 07 C3). |
| 60 | Junior swarm: "`auto/*`-routed specialists" | "`custom/*`-routed specialists" (same reason). |
| 55 | "Model whitelist: Haiku, Sonnet, Opus, Fable only." | "Haiku and Sonnet; Opus and Fable OPEN, whitelisted only if the live model list shows them" (Server.md item 14; 05 Q11). |
| 63 | "reach Claude through Anthropic ... and/or FreeLLMAPI" | Pending C14. |
| 75 | "Mood Board / knowledge-graph visual layer" | "The Constellation (formerly the Mood Board)" (19 C6). Cosmetic. |
| 103 | Company Memory: "Overlaps with Vault/decision-log discipline." | Pending C38: "Vault-backed decision chain; no free-standing company facts." |
| 115 | "The Nigerian Prince Protocol (scam-baiting)" | Pending the rename question in 22 Q8; the optional-behind-wizard rule stands. |

### docs/deep-dives/Bazaar.Server.md

| Where | Quote | Correction |
|---|---|---|
| §3 transport row (line 37) | "**WebSocket** (raw, not Socket.io), Protobuf message schemas" | "WebSocket (raw); message format OPEN (shared C# contracts with System.Text.Json vs Protobuf) until spike S6." The cell header still names Protobuf while its text says reopened (02, 03, 35). |
| §3 search row (line 35) | "ParadeDB's `pg_search` | **DECIDED**" | "Hybrid search behind one interface: Postgres full-text plus pgvector on Windows, `pg_search` on Linux." The leading DECIDED applies to the old single choice. |
| §3 cache row (line 36) | "Valkey on Linux, Garnet (candidate) on Windows" | Pending C07: add "v1 default is Postgres LISTEN/NOTIFY; Garnet only if a benchmark needs it". |
| §3 database row | "monthly partitioning is the future scaling lever" | "hash partitioning by `gc_id` if ever needed (04); do not partition by time" (C10). Also mark "standard indexing ... is sufficient" as unverified until spike S3. |
| §2 | "No installation on the upstream repo required" | Pending C13. |
| §2 | "libgit2 via LibGit2Sharp, not shelling out to the `git` CLI" | Pending C12. |
| Open Questions | item 11 appears before item 10; item 6 repeats "routing Pro/Max subscription credentials through a gateway is not permitted" twice | Reorder; delete the duplicate clause. |
| item 11 | "routes most traffic, including Anthropic API traffic, through it" | Pending C14. |

### docs/deep-dives/Bazaar.Windows.md

| Line | Quote | Correction |
|---|---|---|
| 12 | "(replacing Notepad / the default terminal)" | Delete the parenthetical; the same bullet's CHANGED note says Bazaar only registers as a handler and the user chooses (35 S10). |
| 23 | "genuinely equivalent to what Playwright gives web development" | "equivalent for in-app rendering and logic; it cannot cover WebView, ConPTY, IME, window management or handler registration" (27 C7, 28 C6). |
| 25 | "Terminal and Monaco/WebView2 embedding both verified as real and available under Avalonia ... production-grade ConPTY-based terminal control libraries" | "Unverified: no source shows Monaco in an Avalonia WebView; terminal libraries are candidates only" (27 C1, 28 C2, C5). The inline OPEN correction in the same line should replace the "verified" claim. |
| 39 | "`/shared` ... the Protobuf schemas defining the WebSocket message format" | "`Bazaar.Contracts` ... wire format OPEN until spike S6." |
| 40 | "`Bazaar.UI.Core` ... `/client/shared-ui` ... heads (`Bazaar.Windows`, `Bazaar.Android`, `Bazaar.Linux`)" | `Bazaar.Client.Core` and `Bazaar.Client.Windows/.Android/.Linux`, as owner-approved in CLAUDE.md. |
| 40 | "DirectML vs QNN" (as reported by 28 C8 and 31 C5) | Verified: no such wording remains. Line 40 already reads "OpenVINO on Intel through Windows ML, QNN on Qualcomm; DirectML is legacy GPU only". No edit needed; delete the matching conflict items from 28 and 31. |
| 46 | "[DECIDED] ... composition-based embedding via `CoreWebView2CompositionController`" | Add "no verified Avalonia path; see spike S12" (28 C1). |

### docs/deep-dives/Bazaar.Soul.md

| Line | Quote | Correction |
|---|---|---|
| 12, 28 | Passport "may be mounted ... as a per-agent scoped folder"; "the one scoped mounted folder" | Pending C02 (and 16 Q2 on a second share). |
| 26 | "one golden base image (Server Core + WSL2 + tooling, installed once)" | Update to the two-image model (Windows image and Linux image); the WSL2 design is marked SUPERSEDED on line 25 (16 C3). |
| 43 | "sub-second boot targets" | Delete; the same entry's research note says the figure has no source (16 C3). |
| 51 | Open question 3: "Cloud Hypervisor vs. Hyper-V for Windows guests ... needs direct testing" | Resolved by line 43: Cloud Hypervisor has no documented Windows-host path, Hyper-V is the only one; remove the question. |
| 54 | "VM topology — decided 2026-10-24: two VMs per agent" | Pending C03 (CLAUDE.md still says OPEN). |
| Marketplace | Status "PROPOSED" here vs "DECIDED in nature" in Bazaar.md | Align the two (07 C2, 33 C5); owner answer is 07 Q2. |

### docs/deep-dives/Bazaar.Constellation.md

| Line | Quote | Correction |
|---|---|---|
| 30 | "**[DECIDED]** Search: **ParadeDB's `pg_search`** ..." | "[DECIDED] Search: hybrid lexical plus vector behind one interface; Postgres full-text plus pgvector on Windows, `pg_search` on Linux." The lead sentence still reads as a single `pg_search` decision; the 2026-10-24 update is appended after it (18 C2). |

### docs/deep-dives/Bazaar.Linux.md

| Line | Quote | Correction |
|---|---|---|
| 15 | "the shared UI code (`Bazaar.UI.Core`, see Bazaar.Windows Section 3)" | `Bazaar.Client.Core` (27 C5). |

### CLAUDE.md

| Quote | Correction |
|---|---|
| "No web *client*, ever (a read-only server status page is allowed)." | Add Server.md §1's two decided clarifications: a narrow interactive sign-in page is allowed, and native clients may embed Monaco (C42). |
| "cache and pub/sub (Garnet on Windows, Valkey on Linux)" | Pending C07: Garnet is a candidate, unverified; v1 default is Postgres NOTIFY. |
| "The server does all compute; clients render." | Pending C17: list the decided exceptions (on-device inference, Studio relay, local editor and terminal hosts, fold arrangement). |
| "Agent VM topology is OPEN: benchmark nested WSL2 vs two VMs per agent" | Pending C03. |
| "Claude lane: Anthropic API keys only, Haiku-heavy budget." | Pending C14; reconcile with Bazaar.md line 63. |
| "Clients: Avalonia (shared `UI.Core` library plus thin Windows, Android, Linux heads)" | `Bazaar.Client.Core`; the Projects bullet already maps it. |
| (missing) | Pending C37: add approved names (`Bazaar.Server.Providers.*`, `Bazaar.Client.LocalInference`, `Bazaar.Testing`), Velopack, the xUnit pin, and the "server verifies, never self-reports" rule. |
| (missing) | Pending C12 and C13: the decided LibGit2Sharp and fork-and-PR items are not in CLAUDE.md but sit in Server.md §2 and are contradicted by design docs. |

### Hygiene in the design drafts (not the deep-dives)

Several drafts still cite placeholder file names or "not yet written" docs: 06 header (`01-group-chats`, `03-model-gateway`, `04-soul-memory`, `05-work-life`), 15 header (`01-gc-messaging`, `03-gateway-and-tool-dispatch`, `06-vm-backends`, `09-email`, `11-vault-and-memory`, `13-android-client`), 02 and 04 headers ("TBD", "design docs 01 to 03 not yet written"), and 26 cites "01 section 10", which does not exist. Use the numbering in `README.md`. In 15 and 20, "gateway" means tool dispatch; in 05 it means the model gateway; rename one.
