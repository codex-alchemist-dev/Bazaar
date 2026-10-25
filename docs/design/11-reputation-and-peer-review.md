# Reputation and Peer Review

Status: DRAFT for owner review (2026-10-25) · Owner questions: 15
Depends on: `docs/deep-dives/Bazaar.md` §5 (reputation, Council), `Bazaar.Soul.md` (reputation lives in the Soul), `Bazaar.Server.md` (Postgres, Valkey cache), `docs/implementation-plan.md`
Used by: Council (not yet designed; reads reputation only if owner allows), Talent Pool and promotion (`Bazaar.md` §4), `Bazaar.Windows.md` (profile cards), Kanban and PR flow (outcome source; no design doc yet)

## Purpose
Gives each agent a per-skill record of how its work has gone, so delegation can shift toward proven strengths. Peer reviews (cheap, noisy) are combined with hard outcomes (slow, sparse, authoritative), and the design assumes LLM reviewers are gameable.

## Decided inputs
- Agents evaluate each other's work; delegation shifts toward proven strengths (`Bazaar.md` §5).
- Scores combine peer reviews and hard outcomes (CI results, reverts, owner accept or reject). Outcomes weigh more and can override a peer-review disagreement. Weights OPEN (`Bazaar.md` §5, 2026-10-24).
- Council votes are independent first; tally and reasons are revealed together, at most one follow-up round (`Bazaar.md` §5).
- Resource Economy REJECTED. No money in reputation; limits are quotas and the Work-Life Simulation (`Bazaar.md` §4).
- Each agent owns its reputation; no shared brain (`Bazaar.md` §5).
- Durable Soul data lives outside the VM disk (`Bazaar.Soul.md` §2). See Conflicts 1.
- GC history is append-only; edits keep every version (`Bazaar.md` §2). Review rationale follows the same rule.
- Profile card anatomy DECIDED; layout OPEN (`Bazaar.Windows.md`).
- Talent Pool and Genetic Memory Transfer are in the v1 scope; design still to be written (`Bazaar.md` §4, 2026-10-24).
- Per-agent model whitelists; no global `auto/*` (`CLAUDE.md`).

## Design
Components [PROPOSED]:
1. **Review capture.** A reviewer agent submits a structured review when a card enters Review.
2. **Outcome capture.** The server ingests GitHub App webhooks (check runs, PR merge, revert, post-merge defects) and owner actions from the client (accept, reject).
3. **Attribution.** Each outcome is attributed to the author, approvers or reviewers, per tag.
4. **Ledger.** Append-only tables. Corrections are new rows that supersede old ones.
5. **Projection.** A score per agent per tag, recomputed on new evidence, snapshotted in Postgres and cached in Valkey.
6. **Reads.** Delegation suggestions, profile cards, Talent Pool, promotion.

Tags [PROPOSED]: a controlled list of about 12 (architecture, testing, debugging, shaders, ui, data-access, build-tooling, docs, security, performance, engine-roblox, engine-godot). Tags come from path rules on touched files and card labels. A reviewer may add one tag.

### Evidence and weights [PROPOSAL, weights OPEN]
Each evidence item has x in [0,1] (1 = good) and weight w. The decay factor is `0.5^(age / 180 days)` applied to w.

| Source | x | w | Rule |
|---|---|---|---|
| Owner accept / reject | 1 / 0 | 1.0 | Every card the owner closes |
| Revert of a merged PR | 0 | 1.0 | Author; approvers get calibration only |
| Post-merge defect within 14 days | 0 | 1.0 | Linked by the defect's PR reference |
| CI first run on a PR | green 1 / red 0 | 0.5 | Only the first run counts |
| Peer review, per reviewer (max 2 per PR) | rubric rescaled to [0,1] | 0.2 × reviewer reliability | Blinded, see below |

Score per (agent, tag), with prior weight k = 3 at 0.5:
`S = (Σ w·x + k·0.5) / (Σ w + k)`, and `LCB = S − 1.645·sqrt(S(1−S) / (Σ w + k))` for ranking.

Example: a merged PR is reverted three days later. The author gets x = 0 at w = 1.0, so from S = 0.5 with no history, S becomes 1.5 / 4 = 0.375.

Override rule: when an outcome and the peer majority disagree, the outcome stands. Reviewers in the losing majority get a calibration event.

Reviewer reliability r per tag: agreement between the reviewer's verdict (approve if overall ≥ 4) and later outcomes, not other reviewers. Bayesian rate with prior weight 5 at 0.5, clipped to [0.2, 1.0]. Rubric scores are rescaled against each reviewer's own mean per tag, which counters inflation.

### Anti-gaming [PROPOSED unless noted]
| Threat | Control |
|---|---|
| Review inflation | Per-reviewer rescaling; reviews must cite file and line, else weight 0 |
| Self-preference | Author never reviews own PR; cross-family reviewer preferred; same-family reviews weigh ×0.5 (self-preference verified at abstract level, arXiv 2410.21819) |
| Position bias | Pairwise comparisons run in both orders; disagreement counts as a tie (secondary sources) |
| Reviewers chasing reputation | Reviewers never see scores; agents see bands only |
| Hivemind agreement | Pairwise agreement above threshold reduces both reviewers' weights (secondary) |
| Reciprocal approval rings | Approvals between a pair above baseline are down-weighted |
| Fake green CI | CI counts only if the test count did not drop and test files were not edited unflagged (harness checks) |
| Prompt injection via review text | Review text is data; reviewers have no write tools |

Blind review: the review packet, built server-side, strips author name and model badge. The GC keeps them (DECIDED).

### How delegation reads reputation
- **Advisory only.** When an agent wants help on tag T, the composer shows ranked suggestions (LCB, n_eff, last 90 days). The agent still chooses whom to @mention. No automatic routing.
- **Cold start.** Agents with n_eff < 5 show as Unproven and get an owner-set share of low-risk work.
- **Council.** Reputation does not weight votes in v1 (no evidence supports it). It is shown next to the vote.

### Talent Pool and promotion [PROPOSED]
- Promotion to "proven in T" needs LCB ≥ 0.6, n_eff ≥ 10, at least 3 distinct PRs over at least 30 days. Demotion when LCB < 0.5 for 30 days. Both are logged events.
- Promotion grants a badge and suggestion priority only. It never grants tool access, credentials or permissions.
- Talent Pool templates earn their own record. They inherit nothing.
- Imported Souls start at zero (`Bazaar.md` §4).

### Display
Profile card: top three tags, band (Unproven, Developing, Proven, Strong), n_eff, last outcome date. Owner view adds numbers, the ledger link and history.

### Owner controls
All changes are append-only and carry a reason:
- Add a manual outcome event.
- Mute or unmute a reviewer per tag.
- Reset a tag. A marker is written, history is kept, and scores recompute from after the marker.
- Edit weights. Each edit creates a new `weights_version`, and snapshots record the version used.
- Set the exploration share and promotion thresholds.

Records of reviews and outcomes can never be edited or deleted.

## Data and interfaces
```sql
review (id uuid pk, pr_ref text, card_id uuid, reviewer_agent_id text, author_agent_id text,
        reviewer_family text, author_family text, blinded bool, tag text, overall smallint check (overall between 1 and 5),
        rubric jsonb, rationale text, supersedes_id uuid null, created_at timestamptz)
outcome_event (id uuid pk, kind text check (kind in ('ci_first_run','revert','post_merge_defect',
        'owner_accept','owner_reject','manual')), pr_ref text, x numeric check (x between 0 and 1),
        w numeric, dedupe_key text unique, source_ref text, occurred_at timestamptz, recorded_at timestamptz)
outcome_attribution (outcome_id uuid, agent_id text, role text, tag text, w numeric,
        primary key (outcome_id, agent_id, tag))
reviewer_calibration (reviewer_agent_id text, tag text, r numeric, n int, weights_version int, computed_at timestamptz)
weights_version (version int pk, body jsonb, set_by text, reason text, set_at timestamptz)
score_snapshot (agent_id text, tag text, s numeric, lcb numeric, n_eff numeric, weights_version int,
        computed_at timestamptz, primary key (agent_id, tag, computed_at))
reputation_event (id uuid pk, agent_id text, tag text, kind text, reason text, actor text, at timestamptz)
```

```csharp
public interface IReputationLedger {
    Task AppendReviewAsync(ReviewRecord review, CancellationToken ct);
    Task<bool> AppendOutcomeAsync(OutcomeEvent evt, CancellationToken ct); // false when dedupe_key exists
}
public interface IScoreProjection {
    Task<ScoreView> GetAsync(AgentId agent, SkillTag tag, CancellationToken ct);
    Task<IReadOnlyList<Suggestion>> SuggestAsync(SkillTag tag, int limit, CancellationToken ct);
}
// Pure and deterministic: all scoring lives here so it is testable without a database.
public static class ScoreMath {
    public static ScoreResult Compute(IEnumerable<Evidence> evidence, WeightPolicy policy, DateTimeOffset now);
}
```

Outcome sources: GitHub webhooks (dedupe on delivery ID) and client owner actions. Postgres is authoritative. Valkey holds a rebuildable hash per agent.

## Behaviour and edge cases
- **Idempotency.** `dedupe_key` is `source:delivery-id`. Replays never double count.
- **Late outcomes.** Reverts up to 90 days after merge are attributed, with decayed weight. [PROPOSED]
- **Concurrency.** Projection recompute runs per agent under an advisory lock. Ledger appends are serialised per PR through the unique key.
- **Offline and reconnect.** Clients never write scores. Owner actions are queued and sent with idempotency keys.
- **Model change.** Scores stay with the agent. Each evidence row stores the model family, so profiles can segment by model.
- **Deleted agents.** Ledger rows stay; the agent ID is tombstoned.
- **Re-tagging.** An owner re-tag triggers recompute.
- **Security.** Owner adjustments log the actor. Reviewers cannot write. Review packets are built server-side.

## Testing
No real models or displays are needed.
- **Unit (xUnit v3).** `ScoreMath` golden vectors: no evidence gives 0.5; one revert gives 0.375; decay; prior shrinkage; monotonicity (a good outcome never lowers S); outcome override.
- **Properties.** Order invariance; replaying the same events gives the same snapshot.
- **Anti-gaming simulation.** Synthetic reviewers with inflation, self-preference or collusion, scored through a fake gateway. Assert that weights drop.
- **Position bias.** A fake judge with a fixed position preference must be flagged as a tie in both-order mode.
- **Postgres integration.** Real Postgres 18: migrations, webhook replay idempotency, concurrent appends for one PR.
- **Blinding.** Snapshot test: the packet has no author name or model badge.
- **Avalonia.Headless.** Profile card shows bands to agents and numbers only to the owner.
- **Replay.** Recorded PR fixtures with expected snapshots, approved by the owner.

## Work packages
1. Migrations for the ledger tables. No dependencies.
2. `ScoreMath` plus golden tests. No dependencies.
3. Outcome ingestion from webhook fixtures, with dedupe. Depends on 1.
4. Review capture API and blinded packet builder. Depends on 1.
5. Attribution and reviewer calibration. Depends on 2, 3, 4.
6. Projection, Valkey cache, `SuggestAsync`. Depends on 2, 5.
7. Anti-gaming checks and simulator. Depends on 5.
8. Owner-control endpoints and event log. Depends on 1, 6.
9. Promotion, demotion, Talent Pool link. Depends on 6.
10. Profile card and owner panel in Avalonia. Depends on 6; blocked until layout is decided.

## Conflicts
1. `Bazaar.Soul.md` §2 says reputation is mounted as a scoped folder. The threat-model note says the Passport and audit logs must never sit under a mounted path. This design reads reputation through the platform API only. The Soul doc needs rewriting.
2. Blind review hides the model badge that the GC shows (DECIDED). Resolved as review-packet-only. The GC is unchanged.
3. CI measures tests, not quality, and can be gamed by weakening tests. The test-count guard is a risk, not a fix.
4. Owner accept or reject is the strongest signal, but it reflects the owner's taste. Expect bias toward the owner's preferences.
5. No source tests weighted votes or reputation-weighted teams. The weights and the override rule are inference.
6. Cross-family review needs two or more model families per install, which per-agent whitelists may not allow.
7. Efficiency (tokens, time) is quota use. It is shown as information and not scored, consistent with the Resource Economy rejection.

## Questions for the owner
1. Accept the weight table as a starting point (owner 1.0, revert 1.0, CI 0.5, peer 0.2 × r, prior k = 3)? Default: yes, then calibrate on fixtures. Outcomes must outrank peers, but there is no data yet.
2. Decay half-life 180 days? Default: yes.
3. CI first run counts both ways (green 1, red 0)? Default: yes, with the test-count guard.
4. Revert blame: author only, or approvers too? Default: author in full; approvers get calibration only.
5. Does owner accept or reject apply to every closed card, or only flagged ones? Default: every closed card.
6. Blind review hides author and model badge in the packet? Default: yes.
7. Require a cross-family reviewer when the install has two or more families? Default: prefer it and flag when missing.
8. Does reputation carry across a model change on the same agent? Default: carry, with segmented display.
9. Score efficiency at all? Default: no; show it as information.
10. Start from the 12 tags above? Default: yes; agents may propose tags, the owner approves.
11. Promotion thresholds (LCB ≥ 0.6, n_eff ≥ 10, 3 PRs, 30 days)? Default: yes.
12. Exploration share for Unproven agents? Default: 10% of low-risk work.
13. Should reputation ever weight Council votes? Default: no in v1.
14. Do agents see others' numbers or only bands? Default: bands only, to limit gaming.
15. Can the owner reset a tag, with history kept? Default: yes.
