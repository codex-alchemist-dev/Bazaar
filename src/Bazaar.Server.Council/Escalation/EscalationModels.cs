namespace Bazaar.Server.Council.Escalation;

/// <summary>One revealed ballot shown to the owner on an escalated vote.</summary>
/// <param name="VoterId">The voter's agent id.</param>
/// <param name="Phase">Independent or follow-up.</param>
/// <param name="Choice">The choice cast.</param>
/// <param name="Reasoning">The stated reasoning, as revealed to the electorate.</param>
public sealed record EscalatedBallot(string VoterId, BallotPhase Phase, string Choice, string Reasoning);

/// <summary>
/// An escalated vote awaiting the owner (docs/design/10-council-and-voting.md, Escalation).
/// </summary>
/// <param name="VoteId">Vote id.</param>
/// <param name="GcId">The GC the vote runs in.</param>
/// <param name="SubjectRef">What the vote is about.</param>
/// <param name="Kind">Ballot shape.</param>
/// <param name="MotionClass">Ordinary or bigger change.</param>
/// <param name="Options">The choices the owner may pick: the vote's options, or yes and no for approve/reject.</param>
/// <param name="EligibleCount">Size of the snapshotted electorate.</param>
/// <param name="IndependentBallotCount">Independent ballots cast.</param>
/// <param name="BallotsRevealed">True when the vote passed the reveal. A vote that escalated for no quorum was never revealed.</param>
/// <param name="Ballots">The revealed ballots (empty when <paramref name="BallotsRevealed"/> is false: sealed ballots stay sealed here).</param>
/// <param name="EscalationReason">The reason text of the escalating transition (for example "escalated: tie").</param>
/// <param name="EscalatedAt">When the vote entered Escalated. The age is the current time minus this.</param>
/// <param name="DeadlineAt">The vote's last deadline.</param>
public sealed record EscalatedVote(
    Guid VoteId,
    Guid GcId,
    string SubjectRef,
    VoteKind Kind,
    MotionClass MotionClass,
    IReadOnlyList<string> Options,
    int EligibleCount,
    int IndependentBallotCount,
    bool BallotsRevealed,
    IReadOnlyList<EscalatedBallot> Ballots,
    string? EscalationReason,
    DateTimeOffset EscalatedAt,
    DateTimeOffset DeadlineAt);
