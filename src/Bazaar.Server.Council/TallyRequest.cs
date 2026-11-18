namespace Bazaar.Server.Council;

/// <summary>Everything the tally needs for one decision point. Pure input; no storage or clock.</summary>
/// <param name="Kind">Ballot shape.</param>
/// <param name="MotionClass">Ordinary or bigger change.</param>
/// <param name="Round">Independent (reveal) or follow-up.</param>
/// <param name="EligibleVoterIds">Eligible electorate after exclusions, fixed at open.</param>
/// <param name="Ballots">Sealed ballots from this vote so far.</param>
/// <param name="Options">Option ids for <see cref="VoteKind.OptionChoice"/> (2 to 6, distinct). Must be empty for <see cref="VoteKind.ApproveReject"/>.</param>
/// <param name="QuorumMin">Minimum ballots cast for quorum (council.quorum_min). At least 1.</param>
public sealed record TallyRequest(
    VoteKind Kind,
    MotionClass MotionClass,
    TallyRound Round,
    IReadOnlySet<string> EligibleVoterIds,
    IReadOnlyList<Ballot> Ballots,
    IReadOnlyList<string> Options,
    int QuorumMin = Tally.DefaultQuorumMin);
