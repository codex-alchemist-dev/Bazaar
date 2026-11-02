using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Council.Service;

/// <summary>
/// The Council service over ports (docs/design/10-council-and-voting.md, Lifecycle, Steps, Recording, Escalation).
/// Pure rules come from <see cref="VoteStateMachine"/> and <see cref="Tally"/>. Storage, GC appends and the Vault writer
/// are ports. Every state change is one store transition plus one <c>council.transition</c> GC event.
/// </summary>
/// <remarks>
/// <para>GC event payloads (snake_case JSON; keys are the idempotency keys):</para>
/// <list type="bullet">
/// <item><description><c>council.open</c>, key <c>open:&lt;voteId&gt;</c>: <c>vote_id</c>, <c>gc_id</c>, <c>subject_ref</c>,
/// <c>kind</c>, <c>motion_class</c>, <c>options</c>, <c>proposer_id</c>, <c>subject_author_id</c>, <c>eligible_voter_ids</c>,
/// <c>deadline_at</c>, <c>reveal_seed</c>. No reasoning.</description></item>
/// <item><description><c>council.transition</c>, key <c>transition:&lt;voteId&gt;:&lt;version&gt;</c>: <c>vote_id</c>,
/// <c>from</c>, <c>to</c>, <c>reason</c>, <c>version</c>, <c>deadline_at</c>, <c>extensions</c>. Opening writes no
/// transition; <c>council.open</c> is the record of version 1.</description></item>
/// <item><description><c>council.reveal</c>, key <c>reveal:&lt;voteId&gt;</c>: <c>vote_id</c>, <c>gc_id</c>,
/// <c>reveal_seed</c>, <c>reason_order</c> (voter ids), <c>tally</c> (<c>outcome</c>, <c>choice</c>,
/// <c>escalation_reason</c>, <c>counts</c>) and <c>reasons</c> (<c>voter_id</c>, <c>choice</c>, <c>reasoning</c>) in
/// reason order. This is the one place reasons are shown before the decision.</description></item>
/// <item><description><c>council.decision</c>, key <c>decision:&lt;voteId&gt;</c>: <c>vote_id</c>, <c>gc_id</c>,
/// <c>decided_by</c> (<c>tally</c> or <c>owner</c>), <c>choice</c>, <c>owner_reasoning</c>, <c>vault_decision_no</c>,
/// <c>final_counts</c>, <c>voters</c> (<c>voter_id</c>, <c>independent_choice</c>, <c>followup_choice</c>,
/// <c>final_choice</c>, <c>reasoning</c> of the final ballot).</description></item>
/// </list>
/// </remarks>
public sealed class CouncilService : ICouncilService
{
    /// <summary>Attempts at one transition before <see cref="ConcurrencyException"/>. Each retry reloads the vote.</summary>
    private const int _maxTransitionAttempts = 3;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ICouncilStore _store;
    private readonly ICouncilGcEvents _gcEvents;
    private readonly IGcMembership _membership;
    private readonly TimeProvider _clock;
    private readonly CouncilOptions _options;
    private readonly ICouncilDecisionWriter? _decisionWriter;

    /// <summary>Creates the service. The decision writer is optional until the Vault adapter exists (WP7).</summary>
    /// <exception cref="ArgumentException">The options are out of range.</exception>
    public CouncilService(
        ICouncilStore store,
        ICouncilGcEvents gcEvents,
        IGcMembership membership,
        CouncilOptions options,
        TimeProvider clock,
        ICouncilDecisionWriter? decisionWriter = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gcEvents = gcEvents ?? throw new ArgumentNullException(nameof(gcEvents));
        _membership = membership ?? throw new ArgumentNullException(nameof(membership));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _decisionWriter = decisionWriter;
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<Guid> OpenAsync(string callerId, MotionDraft motion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(motion.SubjectRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(motion.ProposerId);
        if (motion.GcId == Guid.Empty)
        {
            throw new ArgumentException("A vote needs a GC.", nameof(motion));
        }

        ValidateOptionList(motion.Kind, motion.Options);
        if (!string.Equals(callerId, motion.ProposerId, StringComparison.Ordinal))
        {
            throw new CouncilRejectedException("Only the proposer can open a vote on the motion.");
        }

        var now = _clock.GetUtcNow();
        if (motion.Deadline <= now)
        {
            throw new CouncilRejectedException("The motion deadline has already passed.");
        }

        var members = await _membership.ListMembersAsync(motion.GcId, ct);
        var electorate = BuildElectorate(members, motion);
        var record = new CouncilVoteRecord(
            VoteId: Guid.CreateVersion7(now),
            GcId: motion.GcId,
            SubjectRef: motion.SubjectRef,
            Kind: motion.Kind,
            MotionClass: motion.MotionClass,
            State: VoteState.Collecting,
            Version: 1,
            Options: motion.Options.ToArray(),
            DeadlineAt: Earlier(motion.Deadline, now.AddHours(_options.VoteDeadlineCapHours)),
            Extensions: 0,
            EligibleVoterIds: electorate,
            ProposerId: motion.ProposerId,
            SubjectAuthorId: motion.SubjectAuthorId,
            RevealSeed: RandomSeed(),
            VaultDecisionNo: null,
            CreatedAt: now);

        await _store.InsertVoteAsync(record, ct);
        await AppendAsync(
            record.GcId,
            "council.open",
            new
            {
                VoteId = record.VoteId,
                GcId = record.GcId,
                SubjectRef = record.SubjectRef,
                Kind = Wire(record.Kind),
                MotionClass = Wire(record.MotionClass),
                Options = record.Options,
                ProposerId = record.ProposerId,
                SubjectAuthorId = record.SubjectAuthorId,
                EligibleVoterIds = record.EligibleVoterIds.Order(StringComparer.Ordinal).ToArray(),
                DeadlineAt = record.DeadlineAt,
                RevealSeed = record.RevealSeed,
            },
            $"open:{record.VoteId:D}",
            ct);
        return record.VoteId;
    }

    /// <inheritdoc />
    public async Task SubmitBallotAsync(Guid voteId, string voterId, Ballot ballot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ballot);
        ArgumentException.ThrowIfNullOrWhiteSpace(voterId);
        if (!string.Equals(ballot.VoterId, voterId, StringComparison.Ordinal))
        {
            throw new CouncilRejectedException("The ballot names a different voter.");
        }

        var vote = await LoadAsync(voteId, ct);
        if (!VoteStateMachine.CanAcceptBallot(vote.State, ballot.Phase))
        {
            throw new CouncilRejectedException(
                $"The vote is {Wire(vote.State)} and does not accept {Wire(ballot.Phase)} ballots.");
        }

        if (!vote.EligibleVoterIds.Contains(voterId))
        {
            throw new CouncilRejectedException("The agent is not eligible to vote on this motion.");
        }

        if (!IsValidBallotChoice(vote, ballot.Choice))
        {
            throw new CouncilRejectedException($"Choice '{ballot.Choice}' is not valid for this vote.");
        }

        ValidateReasoning(ballot.Reasoning, requireSentences: true);

        // TaintedAtWrite is false until the taint source (doc 15) is wired into the service. Recorded ballots would
        // otherwise be flagged wrongly, so the value is explicit rather than inferred.
        var stored = new StoredBallot(
            voteId,
            voterId,
            ballot.Phase,
            ballot.Choice,
            ballot.Reasoning,
            TaintedAtWrite: false,
            _clock.GetUtcNow());
        if (!await _store.TryInsertBallotAsync(stored, ct))
        {
            throw new CouncilRejectedException("A ballot for this phase is already recorded for this voter.");
        }

        var ballots = await _store.ListBallotsAsync(voteId, ct);
        if (!AllEligibleCast(vote, ballots, ballot.Phase))
        {
            return;
        }

        if (ballot.Phase == BallotPhase.Independent)
        {
            await RevealAsync(voteId, VoteEvent.AllEligibleCast, "all eligible ballots cast", ct);
            return;
        }

        var current = await LoadAsync(voteId, ct);
        if (current.State == VoteState.FollowUp)
        {
            await CompleteFollowUpAsync(current, ct);
        }
    }

    /// <inheritdoc />
    public async Task ProcessDeadlineAsync(Guid voteId, CancellationToken ct)
    {
        var vote = await LoadAsync(voteId, ct);
        var now = _clock.GetUtcNow();
        switch (vote.State)
        {
            case VoteState.Collecting when now >= vote.DeadlineAt:
                await ProcessCollectingDeadlineAsync(vote, ct);
                break;

            case VoteState.Revealed:
                // A crash after the reveal transition leaves the vote here. Completing the reveal is safe to repeat.
                await CompleteRevealAsync(vote, ct);
                break;

            case VoteState.FollowUp when now >= vote.DeadlineAt:
                await CompleteFollowUpAsync(vote, ct);
                break;
        }
    }

    /// <inheritdoc />
    public async Task ResolveEscalationAsync(Guid voteId, string optionChoice, string? reasoning, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionChoice);
        ValidateReasoning(reasoning, requireSentences: false);

        var vote = await LoadAsync(voteId, ct);
        if (vote.State != VoteState.Escalated)
        {
            throw new CouncilRejectedException("Only an escalated vote can be resolved by the owner.");
        }

        if (!IsValidBallotChoice(vote, optionChoice))
        {
            throw new CouncilRejectedException($"'{optionChoice}' is not one of the options of this vote.");
        }

        var decided = await TransitionAsync(
            voteId,
            _ => new TransitionPlan(VoteEvent.OwnerResolved, $"owner resolved: {optionChoice}"),
            ct)
            ?? throw new CouncilRejectedException("The vote is no longer awaiting the owner.");

        await RecordDecisionAsync(decided, optionChoice, "owner", reasoning, ct);
    }

    /// <inheritdoc />
    public async Task WithdrawAsync(Guid voteId, string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var vote = await LoadAsync(voteId, ct);
        var isProposer = string.Equals(actorId, vote.ProposerId, StringComparison.Ordinal);
        if (!isProposer && !await IsOwnerAsync(vote.GcId, actorId, ct))
        {
            throw new CouncilRejectedException("Only the proposer or the owner can withdraw this vote.");
        }

        _ = await TransitionAsync(
            voteId,
            current => VoteStateMachine.TryNext(current.State, VoteEvent.Withdraw, out _)
                ? new TransitionPlan(VoteEvent.Withdraw, $"withdrawn by {actorId}")
                : null,
            ct)
            ?? throw new CouncilRejectedException("This vote can no longer be withdrawn.");
    }

    private async Task ProcessCollectingDeadlineAsync(CouncilVoteRecord vote, CancellationToken ct)
    {
        var ballots = await _store.ListBallotsAsync(vote.VoteId, ct);
        var cast = ballots.Count(ballot => ballot.Phase == BallotPhase.Independent);
        var quorum = Tally.IsQuorumMet(vote.EligibleVoterIds.Count, cast, _options.QuorumMin);

        // The event is recomputed from each freshly loaded vote, so a retry cannot grant a second extension.
        var moved = await TransitionAsync(vote.VoteId, current =>
        {
            if (current.State != VoteState.Collecting || _clock.GetUtcNow() < current.DeadlineAt)
            {
                return null;
            }

            var evt = VoteStateMachine.DeadlineEvent(quorum, current.Extensions, _options.VoteExtensionsMax);
            return evt switch
            {
                VoteEvent.DeadlineExtended => new TransitionPlan(
                    evt,
                    "deadline extended without quorum",
                    _clock.GetUtcNow().AddHours(_options.VoteDeadlineCapHours),
                    current.Extensions + 1),
                VoteEvent.DeadlineWithoutQuorum => new TransitionPlan(evt, "no quorum after extension"),
                _ => new TransitionPlan(evt, "deadline reached with quorum"),
            };
        }, ct);

        if (moved is not null && moved.State == VoteState.Revealed)
        {
            await CompleteRevealAsync(moved, ct);
        }
    }

    private async Task RevealAsync(Guid voteId, VoteEvent evt, string reason, CancellationToken ct)
    {
        var revealed = await TransitionAsync(
            voteId,
            current => current.State == VoteState.Collecting ? new TransitionPlan(evt, reason) : null,
            ct);

        // Null means another writer moved the vote first. That writer completes the reveal.
        if (revealed is not null)
        {
            await CompleteRevealAsync(revealed, ct);
        }
    }

    private async Task CompleteRevealAsync(CouncilVoteRecord revealed, CancellationToken ct)
    {
        var ballots = await _store.ListBallotsAsync(revealed.VoteId, ct);
        var independent = ballots.Where(ballot => ballot.Phase == BallotPhase.Independent).ToList();
        var decision = DecideRound(
            revealed,
            TallyRound.Independent,
            independent,
            await UnanimityBlockedAsync(revealed, ct));

        var order = RevealOrder.Arrange(
            independent.Select(ballot => ballot.VoterId),
            revealed.RevealSeed,
            _options.ReasonOrder == ReasonOrder.Random);
        var byVoter = independent.ToDictionary(ballot => ballot.VoterId, StringComparer.Ordinal);
        await AppendAsync(
            revealed.GcId,
            "council.reveal",
            new
            {
                VoteId = revealed.VoteId,
                GcId = revealed.GcId,
                RevealSeed = revealed.RevealSeed,
                ReasonOrder = order,
                Tally = new
                {
                    Outcome = Wire(decision.Kind),
                    Choice = decision.Choice,
                    EscalationReason = decision.Reason is null ? null : Wire(decision.Reason.Value),
                    Counts = decision.Counts,
                },
                Reasons = order.Select(voterId => new
                {
                    VoterId = voterId,
                    Choice = byVoter[voterId].Choice,
                    Reasoning = byVoter[voterId].Reasoning,
                }),
            },
            $"reveal:{revealed.VoteId:D}",
            ct);

        await ApplyTallyAsync(revealed, decision, ct);
    }

    private async Task CompleteFollowUpAsync(CouncilVoteRecord vote, CancellationToken ct)
    {
        var ballots = await _store.ListBallotsAsync(vote.VoteId, ct);
        var decision = DecideRound(
            vote,
            TallyRound.FollowUp,
            ballots,
            await UnanimityBlockedAsync(vote, ct));
        await ApplyTallyAsync(vote, decision, ct);
    }

    /// <summary>Moves the vote on from a tally result: decide, run the follow-up round, or escalate.</summary>
    private async Task ApplyTallyAsync(CouncilVoteRecord vote, TallyDecision decision, CancellationToken ct)
    {
        if (decision.Kind == TallyDecisionKind.Decided)
        {
            var decided = await TransitionAsync(
                vote.VoteId,
                _ => new TransitionPlan(VoteEvent.Decide, "decided by tally"),
                ct);
            if (decided is not null)
            {
                await RecordDecisionAsync(decided, decision.Choice ?? throw new InvalidOperationException("A decided tally has a choice."), "tally", null, ct);
            }

            return;
        }

        if (decision.Kind == TallyDecisionKind.FollowUpRequired && _options.FollowupEnabled)
        {
            _ = await TransitionAsync(
                vote.VoteId,
                _ => new TransitionPlan(
                    VoteEvent.RequireFollowUp,
                    "no decisive result; follow-up round",
                    _clock.GetUtcNow().AddHours(_options.VoteDeadlineCapHours)),
                ct);
            return;
        }

        // Escalation. With follow-up disabled, a non-decisive independent tally escalates without a reason from the tally.
        var reason = decision.Reason ?? (vote.MotionClass == MotionClass.BiggerChange
            ? EscalationReason.NotUnanimous
            : EscalationReason.NoDecisiveResult);
        _ = await TransitionAsync(
            vote.VoteId,
            _ => new TransitionPlan(VoteEvent.Escalate, $"escalated: {Wire(reason)}"),
            ct);
    }

    private TallyDecision DecideRound(
        CouncilVoteRecord vote,
        TallyRound round,
        IReadOnlyList<StoredBallot> ballots,
        bool unanimityBlocked)
    {
        var request = new TallyRequest(
            vote.Kind,
            vote.MotionClass,
            round,
            vote.EligibleVoterIds,
            ballots.Select(ballot => new Ballot(ballot.VoterId, ballot.Choice, ballot.Reasoning, ballot.Phase)).ToList(),
            vote.Options,
            _options.QuorumMin);
        var decision = Tally.Decide(request);

        // Under the all-members electorate, a member who cannot vote makes a bigger change not unanimous.
        if (unanimityBlocked && vote.MotionClass == MotionClass.BiggerChange && decision.Kind == TallyDecisionKind.Decided)
        {
            return round == TallyRound.Independent
                ? new TallyDecision(TallyDecisionKind.FollowUpRequired, null, null, decision.Counts)
                : new TallyDecision(TallyDecisionKind.Escalated, null, EscalationReason.NotUnanimous, decision.Counts);
        }

        return decision;
    }

    /// <summary>
    /// Moves a vote with a plan computed from a freshly loaded copy. On a version mismatch it reloads and plans again,
    /// up to <see cref="_maxTransitionAttempts"/> attempts. Returns null when the plan does not apply (nothing to do, or the
    /// event is no longer allowed after a concurrent change). Appends one <c>council.transition</c> per written version.
    /// </summary>
    private async Task<CouncilVoteRecord?> TransitionAsync(
        Guid voteId,
        Func<CouncilVoteRecord, TransitionPlan?> plan,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _maxTransitionAttempts; attempt++)
        {
            var current = await LoadAsync(voteId, ct);
            var step = plan(current);
            if (step is null || !VoteStateMachine.TryNext(current.State, step.Event, out var target))
            {
                return null;
            }

            var deadline = step.Deadline ?? current.DeadlineAt;
            var extensions = step.Extensions ?? current.Extensions;
            var written = await _store.TryTransitionAsync(
                voteId,
                current.Version,
                target,
                step.Reason,
                _clock.GetUtcNow(),
                step.Deadline,
                step.Extensions,
                ct);
            if (!written)
            {
                continue;
            }

            var next = current with
            {
                State = target,
                Version = current.Version + 1,
                DeadlineAt = deadline,
                Extensions = extensions,
            };
            await AppendAsync(
                next.GcId,
                "council.transition",
                new
                {
                    VoteId = next.VoteId,
                    From = Wire(current.State),
                    To = Wire(next.State),
                    Reason = step.Reason,
                    Version = next.Version,
                    DeadlineAt = next.DeadlineAt,
                    Extensions = next.Extensions,
                },
                $"transition:{next.VoteId:D}:{next.Version}",
                ct);
            return next;
        }

        throw new ConcurrencyException(
            $"Council vote {voteId:D} changed on every one of {_maxTransitionAttempts} attempts.");
    }

    private async Task RecordDecisionAsync(
        CouncilVoteRecord decided,
        string choice,
        string decidedBy,
        string? ownerReasoning,
        CancellationToken ct)
    {
        var ballots = await _store.ListBallotsAsync(decided.VoteId, ct);
        var voters = ballots
            .GroupBy(ballot => ballot.VoterId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var independent = group.FirstOrDefault(ballot => ballot.Phase == BallotPhase.Independent);
                var followUp = group.FirstOrDefault(ballot => ballot.Phase == BallotPhase.FollowUp);
                var final = followUp ?? independent;
                return new
                {
                    VoterId = group.Key,
                    IndependentChoice = independent?.Choice,
                    FollowupChoice = followUp?.Choice,
                    FinalChoice = final?.Choice,
                    Reasoning = final?.Reasoning,
                };
            })
            .ToList();
        var finalCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var choiceOfVoter in voters.Select(voter => voter.FinalChoice).OfType<string>())
        {
            finalCounts[choiceOfVoter] = finalCounts.GetValueOrDefault(choiceOfVoter) + 1;
        }

        int? vaultNo = decided.VaultDecisionNo;
        if (vaultNo is null && _decisionWriter is not null)
        {
            var markdown = DecisionMarkdown(decided, choice, decidedBy, finalCounts);
            var written = await _decisionWriter.WriteDecisionAsync(
                decided.VoteId,
                $"council-{decided.VoteId:N}",
                markdown,
                ct);
            await _store.SetVaultDecisionNoAsync(decided.VoteId, written, ct);
            vaultNo = written;
        }

        await AppendAsync(
            decided.GcId,
            "council.decision",
            new
            {
                VoteId = decided.VoteId,
                GcId = decided.GcId,
                DecidedBy = decidedBy,
                Choice = choice,
                OwnerReasoning = ownerReasoning,
                VaultDecisionNo = vaultNo,
                FinalCounts = finalCounts,
                Voters = voters.Select(voter => new
                {
                    voter.VoterId,
                    voter.IndependentChoice,
                    voter.FollowupChoice,
                    voter.FinalChoice,
                    voter.Reasoning,
                }),
            },
            $"decision:{decided.VoteId:D}",
            ct);
    }

    private async Task<bool> UnanimityBlockedAsync(CouncilVoteRecord vote, CancellationToken ct)
    {
        if (_options.UnanimityElectorate != UnanimityElectorate.AllGcMembers)
        {
            return false;
        }

        var members = await _membership.ListMembersAsync(vote.GcId, ct);
        return members.Any(member => !vote.EligibleVoterIds.Contains(member.MemberId));
    }

    private async Task<bool> IsOwnerAsync(Guid gcId, string actorId, CancellationToken ct)
    {
        var members = await _membership.ListMembersAsync(gcId, ct);
        return members.Any(member => member.Kind == GcMemberKind.Owner
            && string.Equals(member.MemberId, actorId, StringComparison.Ordinal));
    }

    private HashSet<string> BuildElectorate(IReadOnlyList<GcMember> members, MotionDraft motion)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal) { motion.ProposerId };
        if (_options.ExcludeAuthor && motion.SubjectAuthorId is not null)
        {
            excluded.Add(motion.SubjectAuthorId);
        }

        if (_options.ExcludeMembershipSubject && motion.MembershipSubjectId is not null)
        {
            excluded.Add(motion.MembershipSubjectId);
        }

        return members
            .Where(member => member.Kind == GcMemberKind.Agent
                || (member.Kind == GcMemberKind.Owner && _options.OwnerVotes))
            .Select(member => member.MemberId)
            .Where(memberId => !excluded.Contains(memberId))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<CouncilVoteRecord> LoadAsync(Guid voteId, CancellationToken ct) =>
        await _store.FindVoteAsync(voteId, ct)
        ?? throw new KeyNotFoundException($"Council vote {voteId:D} does not exist.");

    private async Task AppendAsync(Guid gcId, string kind, object payload, string idempotencyKey, CancellationToken ct) =>
        await _gcEvents.AppendAsync(gcId, kind, JsonSerializer.Serialize(payload, _jsonOptions), idempotencyKey, ct);

    private static bool AllEligibleCast(CouncilVoteRecord vote, IReadOnlyList<StoredBallot> ballots, BallotPhase phase)
    {
        var cast = ballots
            .Where(ballot => ballot.Phase == phase)
            .Select(ballot => ballot.VoterId)
            .ToHashSet(StringComparer.Ordinal);
        return vote.EligibleVoterIds.All(cast.Contains);
    }

    private static bool IsValidBallotChoice(CouncilVoteRecord vote, string choice) =>
        vote.Kind == VoteKind.ApproveReject
            ? choice is ApprovalChoices.Yes or ApprovalChoices.No or ApprovalChoices.Abstain
            : vote.Options.Contains(choice, StringComparer.Ordinal);

    private void ValidateReasoning(string? reasoning, bool requireSentences)
    {
        if (requireSentences && string.IsNullOrWhiteSpace(reasoning))
        {
            throw new CouncilRejectedException("A ballot needs stated reasoning.");
        }

        if (reasoning is null)
        {
            return;
        }

        if (reasoning.Length > _options.ReasonMaxChars)
        {
            throw new CouncilRejectedException($"Reasoning is longer than {_options.ReasonMaxChars} characters.");
        }

        if (requireSentences && reasoning.Count(mark => mark is '.' or '!' or '?') < _options.ReasonMinSentences)
        {
            throw new CouncilRejectedException(
                $"Reasoning needs at least {_options.ReasonMinSentences} sentences.");
        }
    }

    private static void ValidateOptionList(VoteKind kind, IReadOnlyList<string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (kind == VoteKind.ApproveReject)
        {
            if (options.Count != 0)
            {
                throw new ArgumentException("Approve/reject votes take no options.", nameof(options));
            }

            return;
        }

        if (options.Count is < Tally.MinOptions or > Tally.MaxOptions)
        {
            throw new ArgumentException(
                $"Option choice votes need {Tally.MinOptions} to {Tally.MaxOptions} options.",
                nameof(options));
        }

        if (options.Any(string.IsNullOrWhiteSpace) || options.Distinct(StringComparer.Ordinal).Count() != options.Count)
        {
            throw new ArgumentException("Options must be non-blank and distinct.", nameof(options));
        }
    }

    private static string DecisionMarkdown(
        CouncilVoteRecord decided,
        string choice,
        string decidedBy,
        IReadOnlyDictionary<string, int> finalCounts)
    {
        // Reasons are not written to the Vault; they stay in the GC log (erasure in Vault files is OPEN, doc 10).
        var subject = decided.SubjectRef.Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal);
        var tally = new StringBuilder();
        foreach (var (key, count) in finalCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            tally.Append(key).Append(": ").Append(count).Append("; ");
        }

        return $"""
            ---
            vote_id: {decided.VoteId:D}
            gc_id: {decided.GcId:D}
            kind: {Wire(decided.Kind)}
            motion_class: {Wire(decided.MotionClass)}
            decided_by: {decidedBy}
            choice: {choice}
            ---

            # Council decision {decided.VoteId:D}

            Subject: [[{subject}]]
            Choice: {choice} ({decidedBy})
            Final tally: {tally.ToString().TrimEnd()}
            """;
    }

    private static string Wire(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static long RandomSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToInt64(bytes);
    }

    private sealed record TransitionPlan(
        VoteEvent Event,
        string Reason,
        DateTimeOffset? Deadline = null,
        int? Extensions = null);
}
