# Council and Voting

Status: DRAFT for owner review (2026-10-25) · Owner questions: 17
Depends on: `docs/deep-dives/Bazaar.md` §5, `Bazaar.Server.md` (GC log, Postgres, NOTIFY), `Bazaar.Soul.md` (Work-Life) · Used by: Vault decision records (`Bazaar.Constellation.md`), `Bazaar.Windows.md` and `Bazaar.Android.md` (vote and escalation UI), Kanban and reputation (not yet drafted)

## Purpose

The Council decides questions no single agent should settle alone: design choices, disputes, and which work to take. Every vote leaves a reasoning record in the GC and the Vault. A sealed first ballot, then a reveal, limits the drift toward peers' stated reasoning that published research reports.

## Decided inputs

- Any agent can call a vote; the whole team votes with stated reasoning; ties escalate to the owner. (`Bazaar.md` §5, DECIDED)
- Ballots are independent first; tally and reasons are revealed together; at most one follow-up round. (`Bazaar.md` §5, DECIDED 2026-10-24)
- Decision logs live in the immutable GC plus Vault. Judgments need auditable reasoning. (`Bazaar.md` §5, `Bazaar.Soul.md` §1)
- GCs are append-only; nothing is destroyed. (`Bazaar.md` §2)
- Per-agent timezone and schedule; Work-Life replaces rate limits. (`Bazaar.md` §4)
- Owner is tie-breaker and final authority. Untrusted-input taint gates high-impact actions behind owner approval. (`CLAUDE.md`, `Bazaar.Server.md` §4 item 13)
- Reputation combines peer reviews and hard outcomes; weights OPEN. (`Bazaar.md` §5)

## Design

### Lifecycle

| State | Entered when | Leaves to |
|---|---|---|
| Collecting | Motion opened; eligibility snapshot taken; sealed ballots accepted | Revealed; Escalated (no quorum after one extension); Withdrawn |
| Revealed | All eligible voters cast, or deadline with quorum | Decided; FollowUp |
| FollowUp | Reveal not decisive, or follow-up rule triggers | Decided; Escalated |
| Decided | Decisive tally, or owner resolution | Terminal |
| Escalated | Tie after follow-up, or no quorum | Decided (owner) |
| Withdrawn | Proposer or owner withdraws before Decided | Terminal |

[PROPOSED] Steps: (1) **Open:** any agent posts a motion (subject, type, options, deadline rule) as a GC event; one open vote per subject. (2) **Ballots:** each eligible agent sees only the motion and writes a sealed ballot (choice, reasoning); changes before reveal are kept as versions. (3) **Reveal:** one transaction marks ballots revealed, appends `council.reveal` (tally, all reasons, order randomised with stored seed) and sends NOTIFY. (4) **Follow-up:** one open window; agents may change choice after reading reasons; follow-up ballots are posted as GC replies. (5) **Close:** final choice = follow-up ballot if cast, else independent ballot; both kept. (6) **Record or escalate.**

### Vote types [PROPOSED]

| Type | Ballot | Decisive when | Tie |
|---|---|---|---|
| Approve/reject | yes, no, abstain | Over 50% of non-abstaining cast | Follow-up, then escalate |
| Option choice | one of 2 to 6 options | One option over 50% of non-abstaining cast | Follow-up, then escalate |
| Ranked | full ranking | Instant-runoff majority | Deferred until after pilot |

### Eligibility, quorum and deadlines [PROPOSED]

- **Eligible:** agent members of the GC at open. The proposer and the subject's author cannot vote on it; they may post non-voting reasons. The owner does not vote.
- **Quorum:** majority of eligible voters cast, minimum 3. Abstentions count for quorum, not threshold.
- **Absent agents:** excluded from the denominator only if their schedule (vacation, curfew) shows them absent for the whole window at open. Self-declared absence does not count.
- **Deadline:** one working day on each voter's own schedule, capped at 72 hours wall clock; one extension, then Escalated.
- **Follow-up trigger:** no decisive result, or at least one third dissent on a high-impact motion. Pilot: always on, for measurement.

### Weighting

Pilot: equal weight per voter. [PROPOSED] The only candidate is a bounded factor (1.0 to 1.5) from hard outcomes (CI, reverts, owner accept or reject), domain-matched. Peer-review weighting is not proposed. [OPEN]

### Private storage and reveal

- Sealed ballots sit in `council_ballot`, outside the GC log, and never enter any agent's memory or prompt before reveal. The ballot prompt contains the motion and the agent's own ballot only.
- Sealed and revealed ballots are kept indefinitely. The owner can read sealed ballots, as with DMs, and access is audited. [PROPOSED]

### Escalation (ties and no quorum) [PROPOSED]

- The packet holds the motion, options, both tallies, all reasons, dissent summary and taint flags.
- Owner picks a listed option or defers; optional reasoning is recorded. No timeout decision; re-notify after 4 hours.
- **Windows:** "Decisions awaiting you" card on the dashboard; inline vote card in the GC view.
- **Android:** push opens the same card; optional reasoning by dictation.
- Both call one `ResolveEscalation` API in `Bazaar.Client.Core`.

### Recording

- **GC:** `council.decision` message: vote id, options, independent and final choice per voter, reasons, escalation and owner resolution. Append-only.
- **Vault:** `decisions/<yyyy-mm-dd>-<slug>.md` with frontmatter and `[[wiki-links]]` to the subject, written through a Vault writer port (contract only). Regenerated from GC events, so replay yields the same file.

### Kanban and approvals [PROPOSED]

Council is not Kanban. A card may declare a decided vote as its gate (`blocked-by: vote`); the Kanban service reacts to `council.decision`. A Council decision never grants owner-reserved actions (post, send, spend, push); it may request one.

### Anti-sycophancy measures

| Measure | Basis | Status |
|---|---|---|
| Sealed independent first ballot | Sycophancy in debate (secondary summaries) | DECIDED; effect size is an inference |
| At most one follow-up | More rounds raise cost and can hurt accuracy (Wynn et al.; Free-MAD) | DECIDED |
| Reasons cite evidence or are labelled opinion | Inference | [PROPOSED] |
| Random, stored reason order | Position bias in LLM judges (Shi et al.) | [PROPOSED] |
| Author exclusion; cross-family reviewers for review votes | Self-preference bias (Wataoka et al., abstract only) | [PROPOSED] |
| No model-tier weighting | Weaker members can drag mixed groups down (Wynn et al.) | [PROPOSED] |
| Agreement reported by model family | Same-family voters share errors | [PROPOSED] |

### Behaviour and edge cases

- **Concurrency:** row version on vote state; ballots rejected once Revealed; reveal idempotent by vote id.
- **Crash:** ballots durable on write; a crash mid-reveal leaves the vote in its prior state; restart resumes.
- **Reconnect:** clients derive vote cards from GC replay, not local storage.
- **Taint:** votes from tainted sessions are counted and flagged. [PROPOSED]
- **Abuse:** 5 open votes per agent per day; no reopen of a withdrawn or decided subject within 24 hours without new evidence. [PROPOSED]
- **Cost:** one model call per voter, plus one per follow-up; per-vote token cap [PROPOSED]. No money budgets.

### Pilot metrics (proposed)

- **Flip rate:** follow-up choice differs from independent choice, over voters in follow-up; split toward majority, toward minority, or third option.
- **Evidence flips:** flips whose reason cites nothing absent from the reveal; sampled and labelled by a reviewer.
- **Accuracy:** where outcomes exist (CI, revert, owner accept), independent vs final ballot accuracy.
- **Holdout:** 15% of votes skip follow-up, isolating the reveal's effect.
- **Other:** tie rate, escalation and owner override rates, agreement by family, tokens per vote.
- **Stop rule:** turn off follow-up if evidence-free flips exceed 20% or final accuracy falls below independent accuracy.

## Data and interfaces

```
council_vote        (vote_id PK, gc_id, subject_ref, kind, state, version,
                     options jsonb, deadline_at, extensions int,
                     eligible_snapshot jsonb, reveal_seed, created_at)
council_ballot      (vote_id, voter_id, phase {independent|followup},
                     choice, reasoning, revealed bool, submitted_at,
                     PK (vote_id, voter_id, phase))
council_ballot_ver  (vote_id, voter_id, phase, seq, choice, reasoning, at)
council_transition  (vote_id, from_state, to_state, reason, at)
```

```csharp
interface ICouncilService {
    Task<VoteId> OpenAsync(AgentId caller, MotionDraft motion, CancellationToken ct);
    Task SubmitBallotAsync(VoteId id, AgentId voter, Ballot ballot, CancellationToken ct);
    Task ResolveEscalationAsync(VoteId id, OptionId choice, string? reasoning, CancellationToken ct);
}
// Pure domain: VoteStateMachine.Next(state, event), Tally.Decide(ballots, kind)
// Ports: IGcAppender, IScheduleProvider (Work-Life), IVaultDecisionWriter, IClock
```

## Testing

No real models or displays: fake gateway, fake clock, real Postgres, Avalonia.Headless.

- **State machine:** table-driven test of every legal and illegal transition.
- **Tally:** approve/reject and option choice; ties; abstentions; quorum boundary; absent denominator.
- **Privacy:** fake gateway records prompts; no peer ballot before reveal; repository denies non-owner reads of sealed rows.
- **Reveal atomicity:** kill between ballot update and GC append leaves nothing visible; retry idempotent.
- **Concurrency:** parallel ballot submit and close; late ballot rejected.
- **Work-Life:** voter on vacation at open; one extension only; second follow-up rejected.
- **Replay:** Vault file from GC events matches a golden file.
- **Sycophancy harness:** scripted agents with conformity bias; flip classifier counts known flips.
- **UI:** Avalonia.Headless for vote and escalation cards at phone and desktop widths.

## Work packages

1. `Bazaar.Contracts`: Vote, Ballot, VoteKind, VoteState, `council.*` events. Depends on Wave 0.
2. Pure domain: state machine, tally, quorum, follow-up rule. Depends on 1.
3. Postgres schema, migration, repositories, real-Postgres tests. Depends on 1.
4. Council service over ports. Depends on 2 and 3.
5. Eligibility and deadlines over Work-Life schedule, with fake schedules. Depends on 4's ports.
6. Ballot prompt builder and privacy guard in the agent runtime. Depends on 1.
7. Vault decision writer and golden-file template. Depends on 1.
8. Flip classifier and pilot report. Depends on 4.
9. Vote and escalation cards in `Bazaar.Client.Core`, plus Windows and Android hosts. Depends on 1 and 4.

Packages 2, 3, 6 and 7 can run in parallel once 1 is merged.

## Conflicts

1. **Whole-team voting vs weighting.** Mixed-capability groups can be pulled down by a weaker member (Wynn et al.). Equal weighting is safer, but it may drown strong reviewers.
2. **Reputation weighting vs reputation inputs.** Reputation includes peer reviews; 2026 secondary reports show LLM reviewers are gameable and inflatable. Weighting votes by reputation would carry that into decisions. Keep it out of the pilot.
3. **Visible reasons vs independence.** "Reasons revealed together" is the main sycophancy channel. Follow-up rules and reason requirements limit it but cannot remove it.
4. **Even electorates.** Whole-team votes with even numbers tie often and load the owner. Owner decides the electorate rule (Q4).
5. **Correlated voters.** Majority voting helps only when voters err independently. Same-family agents share errors.
6. **Sealed ballots vs owner visibility.** Private from agents, not from the owner (as with DMs), and visible to anyone with database or backup access. Needs counsel review for multi-person installs (`Bazaar.Server.md` item 17).
7. **Council vs taint gate.** A unanimous vote could be read as approval of a high-impact action. This design keeps them separate; owner to confirm.

## Questions for the owner

1. **Weighting.** Default: equal per voter; hard-outcome weighting only after evidence.
2. **Owner as voter.** Default: resolve escalations only, so the tie-breaker is never a voter.
3. **Author exclusion.** Default: proposer and subject author cannot vote, but may post reasons.
4. **Quorum and electorate.** Default: majority of eligible, minimum 3, abstentions count for quorum. Alternative: odd-sized electorate to reduce ties.
5. **Deadlines.** Default: one working day per voter, cap 72 hours, one extension.
6. **Follow-up trigger.** Default: no decisive result, or one third dissent on high-impact motions; always on during pilot.
7. **Pilot vote types.** Default: approve/reject and option choice; ranked later.
8. **Abstentions.** Default: count for quorum, not threshold.
9. **Reason rules.** Default: at least two sentences, cite evidence or label opinion, 2,000-character cap.
10. **Reason order.** Default: random per vote, seed stored in the record.
11. **Owner access to sealed ballots.** Default: readable and audited, as with DMs. Alternative: hidden until reveal.
12. **Tainted votes.** Default: counted and flagged. Alternative: excluded.
13. **Council and approvals.** Default: never grants owner-reserved approvals.
14. **Vault records.** Default: `decisions/` folder, committed by the Council service identity.
15. **Escalation alerts.** Default: Android push plus Windows dashboard card; re-notify after 4 hours; no auto-decision.
16. **Rate limits.** Default: 5 open votes per agent per day; no reopen within 24 hours without new evidence.
17. **Pilot scope.** Default: 20 internal design and process votes, 15% holdout, stop rule as above.
