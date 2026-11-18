namespace Bazaar.Server.Council;

/// <summary>
/// Pure Council tally (docs/design/10-council-and-voting.md, Vote types, Membership and role votes, Eligibility).
/// No storage, clock or model calls. The caller runs the reveal and follow-up and acts on the result.
/// </summary>
public static class Tally
{
    /// <summary>Default council.quorum_min.</summary>
    public const int DefaultQuorumMin = 3;

    /// <summary>Option choice ballots need between these many options (inclusive).</summary>
    public const int MinOptions = 2;

    /// <summary>Option choice ballots allow at most this many options (inclusive).</summary>
    public const int MaxOptions = 6;

    /// <summary>Decides a vote at the given round, or says a follow-up or escalation is required.</summary>
    /// <exception cref="ArgumentException">The request is malformed (unknown voter, bad choice, duplicate ballot, and so on).</exception>
    public static TallyDecision Decide(TallyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var finalChoices = ResolveFinalChoices(request);
        var counts = CountChoices(request, finalChoices);

        if (!IsQuorumMet(request.EligibleVoterIds.Count, finalChoices.Count, request.QuorumMin))
        {
            return new TallyDecision(TallyDecisionKind.Escalated, null, EscalationReason.NoQuorum, counts);
        }

        return request.MotionClass == MotionClass.BiggerChange
            ? DecideBiggerChange(request, finalChoices, counts)
            : DecideOrdinary(request, counts);
    }

    /// <summary>
    /// Quorum: more than half of the eligible voters have cast a ballot (abstentions count), and at least
    /// <paramref name="quorumMin"/> ballots were cast.
    /// </summary>
    public static bool IsQuorumMet(int eligibleCount, int castCount, int quorumMin) =>
        castCount >= quorumMin && castCount * 2 > eligibleCount;

    private static TallyDecision DecideBiggerChange(
        TallyRequest request,
        IReadOnlyDictionary<string, string> finalChoices,
        IReadOnlyDictionary<string, int> counts)
    {
        // Unanimous: every eligible voter cast the same non-abstaining choice. A voter who did not cast a
        // ballot (deadline passed) means the vote is not unanimous.
        var allSame = finalChoices.Count == request.EligibleVoterIds.Count
            && finalChoices.Values.Distinct(StringComparer.Ordinal).Count() == 1;

        if (allSame)
        {
            var choice = finalChoices.Values.First();
            if (!IsAbstain(request.Kind, choice))
            {
                return new TallyDecision(TallyDecisionKind.Decided, choice, null, counts);
            }
        }

        return request.Round == TallyRound.Independent
            ? new TallyDecision(TallyDecisionKind.FollowUpRequired, null, null, counts)
            : new TallyDecision(TallyDecisionKind.Escalated, null, EscalationReason.NotUnanimous, counts);
    }

    private static TallyDecision DecideOrdinary(TallyRequest request, IReadOnlyDictionary<string, int> counts)
    {
        var nonAbstaining = counts
            .Where(pair => !IsAbstain(request.Kind, pair.Key))
            .ToList();
        var total = nonAbstaining.Sum(pair => pair.Value);

        // Decisive: one choice over 50% of non-abstaining ballots.
        var leader = total > 0 ? nonAbstaining.FirstOrDefault(pair => pair.Value * 2 > total) : default;
        if (leader.Key is not null)
        {
            return new TallyDecision(TallyDecisionKind.Decided, leader.Key, null, counts);
        }

        if (request.Round == TallyRound.Independent)
        {
            return new TallyDecision(TallyDecisionKind.FollowUpRequired, null, null, counts);
        }

        // After the follow-up round, a shared top count is a tie; anything else without a majority is
        // escalated as no decisive result. Both go to the owner.
        var topCount = total > 0 ? nonAbstaining.Max(pair => pair.Value) : 0;
        var topShared = total > 0 && nonAbstaining.Count(pair => pair.Value == topCount) > 1;
        var reason = topShared ? EscalationReason.Tie : EscalationReason.NoDecisiveResult;
        return new TallyDecision(TallyDecisionKind.Escalated, null, reason, counts);
    }

    /// <summary>
    /// Each voter's final choice: in the follow-up round, their follow-up ballot if cast, otherwise their
    /// independent ballot. In the independent round, only independent ballots count.
    /// </summary>
    private static Dictionary<string, string> ResolveFinalChoices(TallyRequest request)
    {
        var finalChoices = request.Ballots
            .Where(ballot => ballot.Phase == BallotPhase.Independent)
            .ToDictionary(ballot => ballot.VoterId, ballot => ballot.Choice, StringComparer.Ordinal);

        if (request.Round == TallyRound.FollowUp)
        {
            foreach (var ballot in request.Ballots.Where(ballot => ballot.Phase == BallotPhase.FollowUp))
            {
                finalChoices[ballot.VoterId] = ballot.Choice;
            }
        }

        return finalChoices;
    }

    private static Dictionary<string, int> CountChoices(
        TallyRequest request,
        IReadOnlyDictionary<string, string> finalChoices)
    {
        var counts = ChoicesFor(request).ToDictionary(choice => choice, _ => 0, StringComparer.Ordinal);
        foreach (var choice in finalChoices.Values)
        {
            counts[choice]++;
        }

        return counts;
    }

    private static IEnumerable<string> ChoicesFor(TallyRequest request) =>
        request.Kind == VoteKind.ApproveReject
            ? [ApprovalChoices.Yes, ApprovalChoices.No, ApprovalChoices.Abstain]
            : request.Options;

    private static bool IsAbstain(VoteKind kind, string choice) =>
        kind == VoteKind.ApproveReject && choice == ApprovalChoices.Abstain;

    private static void Validate(TallyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.EligibleVoterIds);
        ArgumentNullException.ThrowIfNull(request.Ballots);
        ArgumentNullException.ThrowIfNull(request.Options);

        if (request.QuorumMin < 1)
        {
            throw new ArgumentException("Quorum minimum must be at least 1.", nameof(request));
        }

        ValidateOptions(request);

        var seen = new HashSet<(string VoterId, BallotPhase Phase)>();
        foreach (var ballot in request.Ballots)
        {
            if (string.IsNullOrWhiteSpace(ballot.VoterId))
            {
                throw new ArgumentException("Ballot voter id must not be blank.", nameof(request));
            }

            if (!request.EligibleVoterIds.Contains(ballot.VoterId))
            {
                throw new ArgumentException($"Voter '{ballot.VoterId}' is not eligible.", nameof(request));
            }

            if (ballot.Phase == BallotPhase.FollowUp && request.Round != TallyRound.FollowUp)
            {
                throw new ArgumentException("Follow-up ballots cannot be tallied in the independent round.", nameof(request));
            }

            if (!ChoicesFor(request).Contains(ballot.Choice, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Choice '{ballot.Choice}' is not valid for this vote.", nameof(request));
            }

            if (!seen.Add((ballot.VoterId, ballot.Phase)))
            {
                throw new ArgumentException(
                    $"Voter '{ballot.VoterId}' has more than one {ballot.Phase} ballot.", nameof(request));
            }
        }
    }

    private static void ValidateOptions(TallyRequest request)
    {
        if (request.Kind == VoteKind.ApproveReject)
        {
            if (request.Options.Count != 0)
            {
                throw new ArgumentException("Approve/reject votes take no options.", nameof(request));
            }

            return;
        }

        if (request.Options.Count is < MinOptions or > MaxOptions)
        {
            throw new ArgumentException(
                $"Option choice votes need {MinOptions} to {MaxOptions} options.", nameof(request));
        }

        if (request.Options.Any(string.IsNullOrWhiteSpace) || request.Options.Distinct(StringComparer.Ordinal).Count() != request.Options.Count)
        {
            throw new ArgumentException("Options must be non-blank and distinct.", nameof(request));
        }
    }
}
