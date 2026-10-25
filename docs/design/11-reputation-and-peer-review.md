# Reputation and Peer Review

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 16
Depends on: 02-wire-protocol-and-sync.md, 04-postgres-schema-and-migrations.md, 05-model-gateway.md, 06-agent-runtime.md, 07-soul-system.md, 10-council-and-voting.md, 15-security-taint-and-approvals.md, 18-vault.md, 21-github-integration.md
Used by: 10-council-and-voting.md, 12-kanban-and-dependencies.md, 25-company-systems-knowledge.md, 28-windows-client.md

## Purpose
Gives each agent a per-skill record of how its work has gone, so delegation can shift toward proven strengths. Peer reviews (cheap, noisy) are combined with hard outcomes (slow, sparse, authoritative), and the design assumes LLM reviewers are gameable. Reputation informs delegation, the Council and the owner. It never promotes anyone by itself.

## Decided inputs
- Agents evaluate each other's work; delegation shifts toward proven strengths (`docs/deep-dives/Bazaar.md` §5).
- Outcomes (CI results, reverts, owner accept or reject) outweigh peer reviews and can override a peer disagreement. Weights OPEN (`Bazaar.md` §5).
- Council votes are independent first; tally and reasons are revealed together, at most one follow-up round. Vote reasoning stays out of Thinking GCs until reveal (`Bazaar.md` §5; round 3, C11).
- Promotion, hiring (Talent Pool) and retirement run as Council votes. A unanimous vote passes without the owner; a split or tied vote on a bigger change goes to the owner. Reputation feeds the debate and is not an approval step (round 9, C32).
- Resource Economy REJECTED. No money in reputation; limits are quotas and the Work-Life Simulation (`Bazaar.md` §4).
- Each agent owns its reputation; no shared brain (`Bazaar.md` §5).
- The per-agent mounted folder holds metadata and a session-cookie profile (round 2, C02). Reputation is therefore read through the platform API and is never mounted.
- Structured scores live in Postgres; durable narrative may live in the Vault (round 11, C38).
- Postgres is authoritative; cache and presence use the cache provider, with GC fan-out on NOTIFY (round 1, C07).
- Agent rows, lifecycle and tombstones belong to 06 (round 1, C04).
- For a PR card, Done means the PR merged; reviewer approval is a flag (round 8, C28). The owner merges in the client and the server performs the merge on signed approval (round 8, C25).
- Owner actions reach the server over the socket (round 6, C15). Reviewers run as tool-less reader sessions (round 6, C18; primitive in 15).
- Blind review: the packet strips author name and model badge; the GC keeps them. Kept as designed in the 2026-10-25 reconciliation brief; no separate log entry.
- Records are append-only; edits keep every version, and review rationale follows the same rule (`Bazaar.md` §2).
- Profile card anatomy DECIDED; layout OPEN (`Bazaar.Windows.md`).
- Talent Pool and Genetic Memory Transfer are in v1 scope (`Bazaar.md` §4).
- Per-agent model whitelists; no global `auto/*` (`CLAUDE.md`).

## Design
Components [PROPOSED]:
1. **Review capture.** A reviewer agent submits a structured review when a card enters Review. The score row goes to Postgres; the rationale is a Markdown note written through the VaultWriter (18), and the row holds its reference.
2. **Outcome capture.** The server collects GitHub outcomes (check runs, PR merge, revert, post-merge defects) and owner actions from the client (accept, reject). By default it polls GitHub Events and check-run APIs, because the home host accepts no inbound connections (21; round 8, C30). Webhooks are used only where an operator has configured an optional tunnel.
3. **Attribution.** Each outcome is attributed to the author, approvers or reviewers, per tag.
4. **Ledger.** Append-only tables. Corrections are new rows that supersede old ones.
5. **Projection.** A score per agent per tag, recomputed on new evidence, snapshotted in Postgres and cached through the cache provider (Valkey on Linux; Garnet on Windows only after spike S2). The cache is rebuildable.
6. **Reads.** Delegation suggestions, profile cards, Talent Pool, promotion candidates, and the Council's debate feed.

Tags [PROPOSED]: a controlled list of about 12 (architecture, testing, debugging, shaders, ui, data-access, build-tooling, docs, security, performance, engine-roblox, engine-godot). Tags come from path rules on touched files and card labels. A reviewer may add one tag; the owner approves new tags.

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
| Prompt injection via review text | Review text is data; reviewers run as tool-less reader sessions (15) |

Blind review: the review packet, built server-side, strips author name and model badge. The GC keeps them (DECIDED).

### How delegation reads reputation
- **Advisory only.** When an agent wants help on tag T, the composer shows ranked suggestions (LCB, n_eff, last 90 days). The agent still chooses whom to @mention. No automatic routing.
- **Cold start.** Agents with n_eff < 5 show as Unproven and get an owner-set share of low-risk work.
- **Council.** Reputation does not weight votes in v1. It is shown next to the vote.

### Promotion, demotion and Talent Pool [PROPOSED thresholds]
- The scorer nominates. "Proven in T" candidates have LCB ≥ 0.6, n_eff ≥ 10, at least 3 distinct PRs over at least 30 days. Demotion candidates have LCB < 0.5 for 30 days. Nominations never change a role.
- The Council votes on each promotion and demotion (round 9, C32). The vote receives the evidence (LCB, n_eff, PR count, last outcome date) and runs independent-first like any other vote (10). Whether a badge-level change counts as a "bigger change" is OPEN (Q15).
- Both nominations and the Council result are logged as `reputation_event` rows.
- Promotion grants a badge and suggestion priority only. It never grants tool access, credentials or permissions.
- Talent Pool hiring is a Council vote; 25 owns the Talent Pool, and this doc supplies evidence only. Talent Pool templates earn their own record and inherit nothing. Imported Souls start at zero (`Bazaar.md` §4).

### Display
Profile card: top three tags, band (Unproven, Developing, Proven, Strong), n_eff, last outcome date. Owner view adds numbers, the ledger link and history.

### Owner controls
Each change is append-only, carries a reason, and writes an entry to the audit log owned by 15 (round 6, C16):
- Add a manual outcome event.
- Mute or unmute a reviewer per tag.
- Reset a tag. A marker is written, history is kept, and scores recompute from after the marker.
- Edit weights. Each edit creates a new `weights_version`, and snapshots record the version used.
- Set the exploration share and promotion thresholds. These set the evidence bar; they do not approve individual promotions, which are Council votes.

Records of reviews and outcomes can never be edited or deleted. Erasure of personal data is handled under the open items in Conflicts.

## Data and interfaces
```sql
review (id uuid pk, pr_ref text, card_id uuid, reviewer_agent_id text, author_agent_id text,
        reviewer_family text, author_family text, blinded bool, tag text, overall smallint check (overall between 1 and 5),
        rubric jsonb, rationale_ref text, supersedes_id uuid null, created_at timestamptz)
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
`rationale_ref` is the Vault path and commit of the rationale note [PROPOSED]. This doc owns these tables (round 6, C16); `agent_id` references 06's agent table.

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

Outcome sources: GitHub (polled by default, webhooks where a tunnel exists; 21) and owner actions over the socket (02). Postgres is authoritative. The cache provider holds a rebuildable hash per agent.

## Behaviour and edge cases
- **Idempotency.** `dedupe_key` is `source:delivery-id` for webhooks and `source:event-id` for polled events [PROPOSED]. Replays never double count.
- **Late outcomes.** Reverts up to 90 days after merge are attributed, with decayed weight [PROPOSED].
- **Concurrency.** Projection recompute runs per agent under an advisory lock. Ledger appends are serialised per PR through the unique key.
- **Offline and reconnect.** Clients never write scores. Owner actions are queued and sent over the socket with idempotency keys.
- **Model change.** Scores stay with the agent. Each review and outcome row stores the model family, so profiles can segment by model. The serving source (native or pooled, 05) is recorded by the gateway, not here.
- **Deleted agents.** Ledger rows stay; the agent ID is tombstoned (06).
- **Re-tagging.** An owner re-tag triggers recompute.
- **Security.** Owner adjustments log the actor and go to 15's audit log. Reviewers cannot write. Review packets are built server-side. A rationale note is readable by its author after submission; the blinded packet is the only blinded artifact.

## Testing
No real models or displays are needed.
- **Unit (xUnit v3).** `ScoreMath` golden vectors: no evidence gives 0.5; one revert gives 0.375; decay; prior shrinkage; monotonicity (a good outcome never lowers S); outcome override.
- **Properties.** Order invariance; replaying the same events gives the same snapshot.
- **Ingestion.** Polled-event and webhook fixtures produce the same ledger rows and dedupe identically.
- **Anti-gaming simulation.** Synthetic reviewers with inflation, self-preference or collusion, scored through a fake gateway. Assert that weights drop.
- **Position bias.** A fake judge with a fixed position preference must be flagged as a tie in both-order mode.
- **Council gate.** The projection never changes a vote tally, and a promotion is never applied without a recorded Council result.
- **Postgres integration.** Real Postgres 18: migrations, webhook and poll replay idempotency, concurrent appends for one PR.
- **Blinding.** Snapshot test: the packet has no author name or model badge.
- **Avalonia.Headless.** Profile card shows bands to agents and numbers only to the owner.
- **Replay.** Recorded PR fixtures with expected snapshots, approved by the owner.

## Work packages
1. Migrations for the ledger tables. No dependencies.
2. `ScoreMath` plus golden tests. No dependencies.
3. Outcome ingestion from polled-event and webhook fixtures, with dedupe. Depends on 1.
4. Review capture API, rationale note through the VaultWriter, and blinded packet builder. Depends on 1.
5. Attribution and reviewer calibration. Depends on 2, 3, 4.
6. Projection, cache provider integration, `SuggestAsync`. Depends on 2, 5.
7. Anti-gaming checks and simulator. Depends on 5.
8. Owner-control endpoints and event log. Depends on 1, 6.
9. Promotion and demotion nominations, Council hand-off, Talent Pool link. Depends on 6 and 10.
10. Profile card and owner panel in Avalonia. Depends on 6; blocked until layout is decided.

## Conflicts
1. **For 07 (Soul).** `Bazaar.Soul.md` §2 still lists reputation under the Passport's scoped mount. Under round 2 (C02) the mount holds metadata and cookies only, so reputation is read through the API. The Soul doc must be corrected. The threat-model rule that audit logs never sit under a mounted path still applies.
2. **For 04 (schema).** Drop 04's outline of `reputation_event` and use this doc's tables (round 6, C16).
3. **For 10 (Council).** The feed is display-only. 10 must define "eligible voter" for the unanimous rule and what counts as a "bigger change" (round 9 FOLLOW-UP, C32). Promotion and demotion nominations depend on that definition.
4. **For 18 (Vault).** Rationale notes need a read scope (author, owner, linked GC). Git has no ACLs, so this is part of round 11 (C38) per-agent privacy.
5. **For 21 (GitHub).** Polled Events and check-run records must carry stable IDs for `dedupe_key`. 21 owns the polling design.
6. **Erasure (round 2, C01; round 11, C38).** Rationale notes and owner reject reasons are append-only and could hold personal data. They need the same answer as 04's per-person encrypted fields, or a Vault erasure policy. Needs counsel.
7. **Cross-family review.** Requires two or more model families per install, which per-agent whitelists (05, 07) may not allow. Claude can now be served by either source (round 4, C14), but that adds no family.
8. **Metrics are noisy.** CI measures tests, not quality, and can be gamed by weakening tests. The test-count guard is a risk control, not a fix. Owner accept or reject reflects the owner's taste, so expect bias toward it. The weights and the override rule are inference: no source tests weighted votes or reputation-weighted teams.

## Questions for the owner
1. Accept the weight table as a starting point (owner 1.0, revert 1.0, CI 0.5, peer 0.2 × r, prior k = 3)? The Council now reads these scores, so the values matter. Default: yes, then calibrate on fixtures. Outcomes must outrank peers, but there is no data yet.
2. Decay half-life 180 days? Default: yes.
3. CI first run counts both ways (green 1, red 0)? Default: yes, with the test-count guard.
4. Revert blame: author only, or approvers too? Default: author in full; approvers get calibration only.
5. Does owner accept or reject apply to every closed card, or only flagged ones? Default: every closed card.
6. Require a cross-family reviewer when the install has two or more families? Default: prefer it and flag when missing.
7. Does reputation carry across a model change on the same agent? Default: carry, with segmented display.
8. Score efficiency at all? Default: no; show it as information, consistent with the Resource Economy rejection.
9. Start from the 12 tags above? Default: yes; agents may propose tags, the owner approves.
10. Promotion thresholds (LCB ≥ 0.6, n_eff ≥ 10, 3 PRs, 30 days) as the evidence bar the Council sees? Default: yes.
11. Exploration share for Unproven agents? Default: 10% of low-risk work.
12. Should reputation ever weight Council votes? Default: no in v1.
13. Do agents see others' numbers or only bands? Default: bands only, to limit gaming.
14. Can the owner reset a tag, with history kept? Default: yes.
15. Is a badge-level promotion or demotion a "bigger change" that the owner can break on a tie (round 9 FOLLOW-UP)? Default: no; the Council decides alone, and a tie leaves the status quo.
16. Can an agent read its own review rationale notes in the Vault after submission? Default: yes; blinding covers only the packet sent to the reviewer.
