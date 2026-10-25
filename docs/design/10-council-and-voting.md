# Council and Voting

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 3
Depends on: `01-gc-data-model.md` (GC log, event kinds), `04-postgres-schema-and-migrations.md` (conventions, per-person encryption), `06-agent-runtime.md` (wake reasons, Thinking GCs), `09-work-life-simulation.md` (schedules, absence), `12-kanban-and-dependencies.md` (task service, `council` gate), `15-security-taint-and-approvals.md` (taint flags, approval tiers), `18-vault.md` (VaultWriter), `24-company-systems-core.md` (interrupt stack) · Used by: `07-soul-system.md` (hire, promote, retire), `11-reputation-and-peer-review.md`, `25-company-systems-knowledge.md` (Talent Pool, Archive), `28-windows-client.md` and `29-android-client.md` (vote and escalation UI), `33-backup-dr-and-export.md`

## Purpose

The Council decides questions no single agent should settle alone: design choices, disputes, which work to take, and membership and role changes (hiring, promotion, retirement). Every vote leaves a reasoning record in the GC and the Vault. A sealed first ballot, then a reveal, limits the drift toward peers' stated reasoning that published research reports.

## Decided inputs

- Any agent can call a vote; the whole team votes with stated reasoning; ties escalate to the owner. Ballots are independent first; tally and reasons are revealed together; at most one follow-up round. (`docs/deep-dives/Bazaar.md` §5)
- Team votes manage members and roles: hiring, promotion and retirement run as Council votes. A unanimous vote passes without the owner; split or tied votes on bigger changes escalate to the owner. (C32, round 9)
- Decision records are `decisions/NNNN-slug.md`, written only through `VaultWriter` (doc 18). (C24, round 7)
- Vault holds durable written artifacts; PostgreSQL holds the append-only GC log and structured operational state. (C38, round 11)
- Personal fields are per-person encrypted payloads from day 1; erasure destroys the key and appends a tombstone. (C01, round 2)
- Vote reasoning is withheld from Thinking GCs until the reveal. (C11, round 3)
- GC log model `gc_event` / `gc_message` / `gc_message_version` with NOTIFY fan-out (C09, C07); task state has one writer, the task service (C28).
- Wake reasons belong to doc 06; interrupts use doc 24's stack; owner interrupts bypass vacation only when tagged `emergency` (C08, C35).
- Taint: external content taints hard; agent text taints softly, propagating only from a tainted source session (C06).
- Owner is tie-breaker and final authority (`CLAUDE.md`). Doc 10 owns the `council_*` tables (C16).

## Design

### Lifecycle

| State | Entered when | Leaves to |
|---|---|---|
| Collecting | Motion opened; eligibility snapshot taken; sealed ballots accepted | Revealed; Escalated (no quorum after one extension); Withdrawn |
| Revealed | All eligible voters cast, or deadline with quorum | Decided; FollowUp |
| FollowUp | Reveal not decisive, or follow-up rule triggers | Decided; Escalated |
| Decided | Decisive tally, or owner resolution | Terminal |
| Escalated | Tie or no quorum (any motion); non-unanimous result after follow-up on a bigger change | Decided (owner) |
| Withdrawn | Proposer or owner withdraws before Decided | Terminal |

For a bigger change, "decisive" means unanimous among eligible voters; any split goes to FollowUp, and a split after follow-up escalates.

[PROPOSED] Steps: (1) **Open:** any agent posts a motion (subject, type, class, options, deadline rule) as a `council.open` event; one open vote per subject. (2) **Ballots:** each eligible agent sees only the motion and writes a sealed ballot (choice, reasoning); changes before reveal are kept as versions. (3) **Reveal:** one transaction marks ballots revealed and appends `council.reveal` (tally, all reasons, order randomised with stored seed) and sends NOTIFY. (4) **Follow-up:** one open window; agents may change choice after reading reasons; follow-up ballots are posted as GC replies. (5) **Close:** final choice = follow-up ballot if cast, else independent ballot; both kept. (6) **Record or escalate.**

### Vote types [PROPOSED]

| Type | Ballot | Decisive when | Tie |
|---|---|---|---|
| Approve/reject | yes, no, abstain | Over 50% of non-abstaining cast | Follow-up, then escalate |
| Option choice | one of 2 to 6 options | One option over 50% of non-abstaining cast | Follow-up, then escalate |
| Ranked | full ranking | Instant-runoff majority | Deferred until after pilot |

Enabled types are set by `council.vote_types_enabled` (approve/reject and option choice; ranked is not built).

### Membership and role votes (C32)

- **Motions:** `hire` (candidate from the Talent Pool, doc 25), `promote`, `retire`, and other role changes. A vote may also propose a change to the Company Constitution (doc 24).
- **Motion class:** `bigger_change` or `ordinary`, set by `council.bigger_change_classes` (Settings below). [PROPOSED] Default: every membership or role motion and every Constitution change is a `bigger_change`. This closes the C32 follow-up on which motions are bigger changes as a per-install setting (DECISIONS-LOG round 13, rule 4); the owner's C32 statement still governs the escalation rule.
- **Rule:** a `bigger_change` passes without the owner only when unanimous; split or tied outcomes escalate. An `ordinary` motion uses the vote-type rules above.
- **Effect:** the Council emits `council.decision`; the owning doc applies the change (doc 07 for Soul and lifecycle state, doc 25 for hiring). This doc writes no agent tables (C04).
- **Context:** reputation (doc 11) is shown beside the vote; it does not weight it in v1.

### Eligibility, quorum and deadlines [PROPOSED]

- **Eligible:** agent members of the GC at open. The proposer, the subject's author and (for membership votes) the subject cannot vote; they may post non-voting reasons. The owner does not vote.
- **Unanimity:** measured over eligible voters after these exclusions (`council.unanimity_electorate`; the stricter option counts all GC members).
- **Quorum:** majority of eligible voters cast, minimum `council.quorum_min` (3). Abstentions count for quorum (`council.abstain_counts_quorum`), never for the threshold.
- **Absent agents:** excluded from the denominator only if doc 09 shows them absent for the whole window at open. Self-declared absence does not count.
- **Deadline:** `council.vote_deadline_working_days` (1) on each voter's own schedule, capped by `council.vote_deadline_cap_hours` (72) wall clock; `council.vote_extensions_max` (1) extension, then Escalated.
- **Follow-up trigger:** no decisive result, or at least `council.followup_dissent_share` (0.33) dissent on a high-impact motion. `council.followup_enabled` is on in the pilot. The one-round cap is fixed (DECIDED).

### Weighting

Equal weight per voter by default: `council.reputation_weight` = 1.0 (no weighting), matching doc 11. The setting may be raised to at most 1.5, a bounded factor from hard outcomes (CI, reverts, owner accept or reject), domain-matched [PROPOSED]. Peer-review weighting is not proposed.

### Private storage and reveal

- Sealed ballots sit in `council_ballot` in Postgres, outside the GC log, and never enter any agent's memory or prompt before reveal. The ballot prompt contains the motion and the agent's own ballot only.
- Ballot reasoning and the reasons in `council.reveal` are personal fields: per-person encrypted (C01). Erasure destroys that voter's key and tombstones their reasoning; the tally and structure stay. Legal sufficiency needs counsel.
- Sealed and revealed ballots are kept as the audit record, except through erasure. The owner can read sealed ballots, as with DMs, and access is audited. [PROPOSED] (Question 1)
- Thinking GCs never show vote reasoning before the reveal (doc 06, C11).

### Escalation (ties, no quorum, bigger-change splits) [PROPOSED]

- The packet holds the motion, options, both tallies, all reasons, dissent summary and taint flags.
- Owner picks a listed option or defers; optional reasoning is recorded. No timeout decision; re-notify every `council.escalation_renotify_hours` (4).
- Alerts use the owner-decision wake kind (C41): a bare hint; the card is fetched over the authenticated connection.
- **Windows:** "Decisions awaiting you" card on the dashboard; inline vote card in the GC view. **Android:** push opens the same card; optional reasoning by dictation.
- Both call one `ResolveEscalation` API in `Bazaar.Client.Core`.

### Recording

- **GC:** `council.open`, `council.reveal`, `council.transition` and `council.decision` are `gc_event` rows, kinds registered in doc 01. `council.decision` holds vote id, options, independent and final choice per voter, reasons, escalation and owner resolution. A readable summary may be a `gc_message` by the Council service identity. [PROPOSED] Append-only.
- **Vault:** `decisions/NNNN-slug.md` (C24). The Council asks `VaultWriter` (doc 18) to write it; the writer assigns NNNN, stored on `council_vote`, so replay regenerates the same file. Frontmatter and `[[wiki-links]]` to the subject. Commit identity follows doc 18.

### Kanban and approvals [PROPOSED]

Council is not Kanban. A card gates on a decided vote with the `council` gate (doc 12); the task service reacts to `council.decision`. A Council decision never grants owner-reserved actions (post, send, spend, push); it may request one through doc 15's approval tiers.

### Anti-sycophancy measures

| Measure | Basis | Status |
|---|---|---|
| Sealed independent first ballot | Sycophancy in debate (secondary) | DECIDED; effect size is an inference |
| At most one follow-up | More rounds can hurt accuracy (Wynn et al.; Free-MAD) | DECIDED |
| Reasons cite evidence or are labelled opinion | Inference | [PROPOSED] |
| Random, stored reason order | Position bias in LLM judges (Shi et al.) | [PROPOSED] |
| Author exclusion; cross-family reviewers for review votes | Self-preference bias (Wataoka et al., abstract only) | [PROPOSED] |
| No model-tier weighting | Weaker members drag mixed groups down (Wynn et al.) | [PROPOSED] |
| Agreement reported by model family | Same-family voters share errors | [PROPOSED] |

### Behaviour and edge cases

- **Wake:** ballot requests reach voters through doc 06's wake reasons (Conflicts). The Council never calls a flat interrupt; any interrupt uses doc 24's push and pop contract.
- **Concurrency:** row version on vote state; ballots rejected once Revealed; reveal idempotent by vote id.
- **Crash:** ballots durable on write; a crash mid-reveal leaves the vote in its prior state; restart resumes.
- **Reconnect:** clients derive vote cards from GC replay, not local storage.
- **Taint:** each ballot records `tainted_at_write` (doc 15). Tainted votes are counted and flagged. [PROPOSED]
- **Abuse:** `council.open_votes_per_agent_day` (5); no reopen of a withdrawn or decided subject within `council.reopen_cooldown_hours` (24) without new evidence. [PROPOSED]
- **Cost:** one model call per voter, plus one per follow-up; per-vote token cap [PROPOSED]. No money budgets.

### Pilot metrics (proposed)

- **Flip rate** (follow-up vs independent choice, by direction) and **evidence flips** (flips whose reason cites nothing absent from the reveal; reviewer-labelled sample).
- **Accuracy** where outcomes exist (CI, revert, owner accept): independent vs final ballot.
- **Holdout:** `council.pilot_holdout_share` (0.15) of votes skip follow-up. **Other:** tie, escalation and override rates; agreement by family; tokens per vote. Pilot size: `council.pilot_target_votes` (20).
- **Stop rule:** turn off follow-up if evidence-free flips exceed `council.pilot_stop_flip_share` (0.20) or final accuracy falls below independent accuracy.

## Data and interfaces

Doc 10 owns these tables (C16). Shared conventions come from doc 04.

```
council_vote        (vote_id PK, gc_id, subject_ref, kind, motion_class {ordinary|bigger_change},
                     state, version, options jsonb, deadline_at, extensions int,
                     eligible_snapshot jsonb, reveal_seed, vault_decision_no int null, created_at)
council_ballot      (vote_id, voter_id, phase {independent|followup}, choice,
                     reasoning (per-person encrypted), revealed bool, tainted_at_write bool,
                     submitted_at, PK (vote_id, voter_id, phase))
council_ballot_ver  (vote_id, voter_id, phase, seq, choice, reasoning (per-person encrypted), at)
council_transition  (vote_id, from_state, to_state, reason, at)
```

```csharp
interface ICouncilService {
    Task<VoteId> OpenAsync(AgentId caller, MotionDraft motion, CancellationToken ct);
    Task SubmitBallotAsync(VoteId id, AgentId voter, Ballot ballot, CancellationToken ct);
    Task ResolveEscalationAsync(VoteId id, OptionId choice, string? reasoning, CancellationToken ct);
}
// Pure domain: VoteStateMachine.Next(state, event), Tally.Decide(ballots, kind, motionClass)
// Ports: IGcAppender (doc 01), IScheduleProvider (doc 09), IVaultDecisionWriter (forwards to VaultWriter, doc 18), IClock
```

### Settings

Registry rules from doc 03 (round 13): settings are per install here, changes are audited (doc 15 `audit_log`), and no setting loosens a DECIDED item (one follow-up round, sealed ballots, hard-taint gating of outward actions).

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `council.reputation_weight` | decimal, 1.0 to 1.5 | 1.0 (none) | install | operator | Yes, audited |
| `council.owner_votes` | bool | false | install | operator | Yes, audited |
| `council.exclude_author` | bool | true | install | operator | Yes, audited |
| `council.exclude_membership_subject` | bool | true | install | operator | Yes, audited |
| `council.unanimity_electorate` | enum: eligible_after_exclusions, all_gc_members | eligible_after_exclusions | install | operator | Yes, audited |
| `council.bigger_change_classes` | list | all membership and role motions, and Constitution changes [PROPOSED] | install | operator; Council may propose | Yes, audited |
| `council.quorum_min` | int, at least 1 | 3 | install | operator | No, audited |
| `council.abstain_counts_quorum` | bool | true | install | operator | No, audited |
| `council.vote_types_enabled` | list | approve_reject, option_choice | install | operator | No, audited |
| `council.vote_deadline_working_days` | int | 1 | install | operator | No, audited |
| `council.vote_deadline_cap_hours` | int | 72 | install | operator | No, audited |
| `council.vote_extensions_max` | int | 1 | install | operator | No, audited |
| `council.followup_enabled` | bool | true (pilot) | install | operator | Yes, audited |
| `council.followup_dissent_share` | decimal | 0.33 | install | operator | No, audited |
| `council.reason_min_sentences` | int | 2 | install | operator | No, audited |
| `council.reason_max_chars` | int | 2000 | install | operator | No, audited |
| `council.reason_order` | enum: random, fixed | random | install | operator | No, audited |
| `council.escalation_renotify_hours` | int | 4 | install | operator | No, audited |
| `council.open_votes_per_agent_day` | int | 5 | install | operator | No, audited |
| `council.reopen_cooldown_hours` | int | 24 | install | operator | No, audited |
| `council.pilot_holdout_share` | decimal | 0.15 | install | operator | No, audited |
| `council.pilot_stop_flip_share` | decimal | 0.20 | install | operator | No, audited |
| `council.pilot_target_votes` | int | 20 | install | operator | No, audited |

## Testing

No real models or displays: fake gateway, fake clock, real Postgres, Avalonia.Headless.

- **State machine:** table-driven test of every legal and illegal transition, including the bigger-change split path.
- **Tally:** approve/reject and option choice; ties; abstentions; quorum boundary; absent denominator; bigger change passes only when unanimous.
- **Membership:** subject and proposer cannot vote; passed `hire` emits `council.decision` and writes no agent table here.
- **Privacy:** fake gateway records prompts; no peer ballot before reveal; non-owner reads of sealed rows denied; no vote reasoning in Thinking GCs before reveal; erasing one voter's key keeps tally and structure.
- **Reveal and concurrency:** kill between ballot update and GC append leaves nothing visible; retry idempotent; parallel submit and close; late ballot rejected.
- **Work-Life:** voter on vacation at open; one extension only; second follow-up rejected.
- **Replay:** Vault file from GC events, with the stored NNNN, matches a golden file. Sycophancy harness: scripted conformity-biased agents; flip classifier counts known flips. UI: Avalonia.Headless vote and escalation cards at phone and desktop widths.

## Work packages

1. `Bazaar.Contracts`: Vote, Ballot, VoteKind, MotionClass, VoteState, `council.*` events. Depends on Wave 0.
2. Pure domain: state machine, tally, quorum, follow-up and bigger-change rules. Depends on 1.
3. Postgres schema and repositories. Depends on 1 and doc 04's encryption helpers.
4. Council service over ports. Depends on 2 and 3.
5. Eligibility and deadlines over the Work-Life schedule. Depends on 4.
6. Ballot prompt builder and privacy guard. Depends on 1.
7. Vault decision writer adapter and golden file. Depends on 1 and doc 18.
8. Flip classifier and pilot report. Depends on 4.
9. Vote and escalation cards in `Bazaar.Client.Core`, plus hosts. Depends on 1 and 4.
10. Membership outcome handoff to docs 07 and 25 (contract only). Depends on 4.

Packages 2, 3, 6 and 7 can run in parallel once 1 is merged.

## Conflicts

1. **Whole-team voting vs weighting.** Mixed-capability groups can be pulled down by a weaker member. Equal weighting is safer but may drown strong reviewers.
2. **Visible reasons vs independence.** Reveal remains the main sycophancy channel; follow-up rules limit it but cannot remove it.
3. **Even electorates.** Even electorates tie often and load the owner. Operators can address this with `council.quorum_min` and `council.owner_votes` (Settings).
4. **Correlated voters.** Majority voting helps only when voters err independently; same-family agents share errors.
5. **Sealed ballots vs owner visibility and backups.** Sealed ballots are readable by the owner and present in backups until the erasure window closes (C01; doc 33). Multi-person installs need counsel review (`Bazaar.Server.md` item 17).
6. **Council vs taint gate.** Unanimous votes can pass membership changes without the owner (C32). This doc keeps them outside the taint gate, which covers outward actions (C06). Owner confirms (Question 2).
7. **Doc 06 (wake reasons).** Ballot requests need a named wake reason (assignment, or a new vote-request reason). Doc 06 must also replace its flat `Interrupt` with doc 24's push contract (C35).
8. **Docs 07 and 11 (promotion).** Doc 07 Q7 ("promotion needs your approval") and doc 11 ("promotion logged, badge only") must both become Council votes (C32).
9. **Doc 04 (tables).** Drop the outlined `vote` and `ballot` tables (C16). Mark `council_ballot.reasoning` and reveal reasons as per-person encrypted (C01).
10. **Doc 01 (kinds).** Register `council.*` event kinds in the kind registry (C19).
11. **Erasure in Vault files (OPEN).** Decision records quote reasons; erasure inside Markdown and git history is OPEN (C38 follow-up).

Resolved in this pass: the reputation-weighting conflict (doc 10 proposed 1.0 to 1.5; doc 11 says none). `council.reputation_weight` defaults to 1.0 per DECISIONS-LOG round 13, so the two docs agree.

## Questions for the owner

1. **Owner access to sealed ballots.** Default: readable and audited, as with DMs. Alternative: hidden until reveal.
2. **Tainted votes and the taint boundary.** Default: tainted votes counted and flagged; membership changes stay outside the taint gate. (Moving them inside would change the hard-taint gate, so this stays with the owner.)
3. **Council and approvals.** Default: never grants owner-reserved approvals; may request one through doc 15's tiers.

MOVED TO SETTINGS: council.reputation_weight, council.owner_votes, council.exclude_author, council.exclude_membership_subject, council.unanimity_electorate, council.bigger_change_classes, council.quorum_min, council.abstain_counts_quorum, council.vote_types_enabled, council.vote_deadline_working_days, council.vote_deadline_cap_hours, council.vote_extensions_max, council.followup_enabled, council.followup_dissent_share, council.reason_min_sentences, council.reason_max_chars, council.reason_order, council.escalation_renotify_hours, council.open_votes_per_agent_day, council.reopen_cooldown_hours, council.pilot_holdout_share, council.pilot_stop_flip_share, council.pilot_target_votes (DECISIONS-LOG round 13)
