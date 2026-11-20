namespace Bazaar.Server.Council.Membership;

/// <summary>
/// Pure mapping from a Council tally to a membership handoff (docs/design/10-council-and-voting.md, Membership and role votes).
/// No storage, clock or model calls.
/// </summary>
public static class MembershipOutcomeMapper
{
    /// <summary>
    /// Maps a tally result to the outcome handed to the owning docs, or null while the vote is still pending.
    /// </summary>
    /// <param name="decision">The tally result.</param>
    /// <param name="motion">The motion's metadata.</param>
    /// <param name="ownerResolution">The owner's resolution, or null when the owner has not resolved the vote.</param>
    /// <param name="vaultDecisionNo">NNNN of the Vault decision file, when one has been written.</param>
    /// <exception cref="ArgumentException">
    /// The choice is not yes or no, or an owner resolution is given for a vote that the tally did not escalate.
    /// </exception>
    public static MembershipOutcome? Map(
        TallyDecision decision,
        MembershipMotionMetadata motion,
        OwnerResolution? ownerResolution = null,
        int? vaultDecisionNo = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(motion);

        if (ownerResolution is not null)
        {
            if (decision.Kind != TallyDecisionKind.Escalated)
            {
                throw new ArgumentException("An owner resolution applies only to an escalated vote.", nameof(ownerResolution));
            }

            return Build(motion, OwnerDecisionFor(ownerResolution.Choice), ownerResolution.Choice, vaultDecisionNo);
        }

        switch (decision.Kind)
        {
            case TallyDecisionKind.Decided:
                // A bigger change passes without the owner only when unanimous. Tally escalates non-unanimous
                // bigger changes, so this is a defensive check: the outcome stays pending rather than Approved.
                if (motion.MotionClass == MotionClass.BiggerChange && !IsUnanimous(decision))
                {
                    return null;
                }

                return Build(motion, DecisionFor(decision.Choice), decision.Choice, vaultDecisionNo);

            case TallyDecisionKind.Escalated:
            case TallyDecisionKind.FollowUpRequired:
            default:
                // Pending: waiting for the owner, or for the follow-up round.
                return null;
        }
    }

    /// <summary>Maps a withdrawn vote. A withdrawn vote has no choice and no Vault decision file.</summary>
    public static MembershipOutcome MapWithdrawn(MembershipMotionMetadata motion)
    {
        ArgumentNullException.ThrowIfNull(motion);
        return Build(motion, MembershipDecision.Withdrawn, choice: null, vaultDecisionNo: null);
    }

    /// <summary>The reasons summary reference for a vote: the <c>council.decision</c> GC event for that vote.</summary>
    public static string ReasonsSummaryRefFor(Guid voteId) => $"council.decision:{voteId:D}";

    private static bool IsUnanimous(TallyDecision decision)
    {
        if (decision.Choice is null)
        {
            return false;
        }

        var nonZero = decision.Counts.Where(pair => pair.Value > 0).ToList();
        return nonZero.Count == 1 && nonZero[0].Key == decision.Choice;
    }

    private static MembershipDecision DecisionFor(string? choice) => choice switch
    {
        ApprovalChoices.Yes => MembershipDecision.Approved,
        ApprovalChoices.No => MembershipDecision.Rejected,
        _ => throw new ArgumentException($"Membership motions decide yes or no, not '{choice}'.", nameof(choice)),
    };

    private static MembershipDecision OwnerDecisionFor(string choice) => choice switch
    {
        ApprovalChoices.Yes => MembershipDecision.OwnerApproved,
        ApprovalChoices.No => MembershipDecision.OwnerRejected,
        _ => throw new ArgumentException($"The owner resolves membership motions yes or no, not '{choice}'.", nameof(choice)),
    };

    private static MembershipOutcome Build(
        MembershipMotionMetadata motion,
        MembershipDecision decision,
        string? choice,
        int? vaultDecisionNo) =>
        new(
            motion.VoteId,
            motion.SubjectAgentId,
            motion.Kind,
            decision,
            choice,
            ReasonsSummaryRefFor(motion.VoteId),
            motion.DecidedAt,
            vaultDecisionNo);
}
