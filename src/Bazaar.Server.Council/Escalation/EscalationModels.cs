namespace Bazaar.Server.Council.Escalation;

/// <summary>One revealed ballot shown to the owner on an escalated vote.</summary>
/// <param name="VoterId">The voter's agent id.</param>
/// <param name="Phase">Independent or follow-up.</param>
/// <param name="Choice">The choice cast.</param>
/// <param name="Reasoning">The stated reasoning, as revealed to the electorate.</param>
/// <param name="TaintedAtWrite">Whether the voter's session was tainted when the ballot was written (doc 15).</param>
public sealed record EscalatedBallot(string VoterId, BallotPhase Phase, string Choice, string Reasoning, bool TaintedAtWrite = false);

/// <summary>One option's count in one round.</summary>
/// <param name="Choice">The option (or yes, no, abstain for approve/reject).</param>
/// <param name="Count">Ballots cast for it in that round.</param>
public sealed record OptionCount(string Choice, int Count);

/// <summary>
/// The dissent summary of the escalation packet (docs/design/10, Escalation): both tallies and how far the electorate was
/// from agreeing. Built only from revealed ballots, so a vote that escalated for no quorum has none.
/// </summary>
/// <param name="Independent">Counts of the independent round, every option listed, abstentions last.</param>
/// <param name="FollowUp">Counts of the follow-up round (empty when none was cast).</param>
/// <param name="LeadingChoice">The choice with the most non-abstaining ballots in the latest round cast, or null on a tie for first.</param>
/// <param name="DissentingBallots">Non-abstaining ballots in the latest round cast against <paramref name="LeadingChoice"/>; all of them when it is null.</param>
/// <param name="NonAbstainingBallots">Non-abstaining ballots in the latest round cast.</param>
/// <param name="DissentShare">Dissenting over non-abstaining ballots, or null with no non-abstaining ballot.</param>
public sealed record DissentSummary(
    IReadOnlyList<OptionCount> Independent,
    IReadOnlyList<OptionCount> FollowUp,
    string? LeadingChoice,
    int DissentingBallots,
    int NonAbstainingBallots,
    double? DissentShare);

/// <summary>
/// The taint flags of the escalation packet (doc 15, <c>tainted_at_write</c>). Counts only, never choices, so they are safe for
/// a vote whose ballots are still sealed.
/// </summary>
/// <param name="TaintedBallots">Ballots written from a tainted session.</param>
/// <param name="TotalBallots">All ballots cast.</param>
/// <param name="TaintedVoters">Voters with at least one tainted ballot.</param>
public sealed record TaintFlags(int TaintedBallots, int TotalBallots, IReadOnlyList<string> TaintedVoters)
{
    /// <summary>True when any ballot was written from a tainted session.</summary>
    public bool AnyTainted => TaintedBallots > 0;
}

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
/// <param name="Dissent">The dissent summary, or null when the ballots were never revealed.</param>
/// <param name="Taint">The taint flags over every ballot cast.</param>
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
    DateTimeOffset DeadlineAt,
    DissentSummary? Dissent = null,
    TaintFlags? Taint = null);
