namespace Bazaar.Server.Council;

/// <summary>
/// Pure vote lifecycle (docs/design/10-council-and-voting.md, Lifecycle and Eligibility, quorum and deadlines).
/// No storage, clock or model calls. The caller stores the state, bumps the extension count, and runs the reveal.
/// </summary>
public static class VoteStateMachine
{
    /// <summary>Whether no further transition is possible.</summary>
    public static bool IsTerminal(VoteState state) => state is VoteState.Decided or VoteState.Withdrawn;

    /// <summary>Applies an event. Returns false and leaves <paramref name="next"/> at the current state when the event is not allowed.</summary>
    public static bool TryNext(VoteState state, VoteEvent evt, out VoteState next)
    {
        var target = (state, evt) switch
        {
            (VoteState.Collecting, VoteEvent.AllEligibleCast) => VoteState.Revealed,
            (VoteState.Collecting, VoteEvent.DeadlineWithQuorum) => VoteState.Revealed,
            (VoteState.Collecting, VoteEvent.DeadlineExtended) => VoteState.Collecting,
            (VoteState.Collecting, VoteEvent.DeadlineWithoutQuorum) => VoteState.Escalated,
            (VoteState.Revealed, VoteEvent.Decide) => VoteState.Decided,
            (VoteState.Revealed, VoteEvent.RequireFollowUp) => VoteState.FollowUp,
            (VoteState.Revealed, VoteEvent.Escalate) => VoteState.Escalated,
            (VoteState.FollowUp, VoteEvent.Decide) => VoteState.Decided,
            (VoteState.FollowUp, VoteEvent.Escalate) => VoteState.Escalated,
            (VoteState.Escalated, VoteEvent.OwnerResolved) => VoteState.Decided,
            (VoteState.Collecting or VoteState.Revealed or VoteState.FollowUp or VoteState.Escalated, VoteEvent.Withdraw)
                => VoteState.Withdrawn,
            _ => (VoteState?)null,
        };

        next = target ?? state;
        return target is not null;
    }

    /// <summary>Applies an event, or throws when the transition is not allowed.</summary>
    /// <exception cref="InvalidOperationException">The event is not allowed from <paramref name="state"/>.</exception>
    public static VoteState Next(VoteState state, VoteEvent evt) =>
        TryNext(state, evt, out var next)
            ? next
            : throw new InvalidOperationException($"Vote event '{evt}' is not allowed in state '{state}'.");

    /// <summary>Whether a ballot of this phase is accepted in this state. Ballots are rejected once revealed.</summary>
    public static bool CanAcceptBallot(VoteState state, BallotPhase phase) =>
        phase switch
        {
            BallotPhase.Independent => state == VoteState.Collecting,
            BallotPhase.FollowUp => state == VoteState.FollowUp,
            _ => false,
        };

    /// <summary>
    /// The event for a deadline. Quorum met reveals the vote. Without quorum, one extension is granted while
    /// <paramref name="extensionsUsed"/> is below <paramref name="extensionsMax"/>; after that the vote escalates.
    /// </summary>
    public static VoteEvent DeadlineEvent(bool quorumMet, int extensionsUsed, int extensionsMax)
    {
        if (quorumMet)
        {
            return VoteEvent.DeadlineWithQuorum;
        }

        return extensionsUsed < extensionsMax ? VoteEvent.DeadlineExtended : VoteEvent.DeadlineWithoutQuorum;
    }

    /// <summary>The event that follows a tally result taken in the current round.</summary>
    public static VoteEvent EventFor(TallyDecisionKind kind) =>
        kind switch
        {
            TallyDecisionKind.Decided => VoteEvent.Decide,
            TallyDecisionKind.FollowUpRequired => VoteEvent.RequireFollowUp,
            TallyDecisionKind.Escalated => VoteEvent.Escalate,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}
