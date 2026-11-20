namespace Bazaar.Server.Council;

/// <summary>What the tally decided at this point.</summary>
public enum TallyDecisionKind
{
    /// <summary>A final choice was reached.</summary>
    Decided,

    /// <summary>No decisive result yet; run the single follow-up round.</summary>
    FollowUpRequired,

    /// <summary>Goes to the owner (docs/design/10-council-and-voting.md, Escalation).</summary>
    Escalated,
}

/// <summary>Why a vote was escalated to the owner.</summary>
public enum EscalationReason
{
    /// <summary>Fewer ballots cast than quorum requires, or not more than half the electorate.</summary>
    NoQuorum,

    /// <summary>Two or more choices share the top count after the follow-up round.</summary>
    Tie,

    /// <summary>No choice over 50% of non-abstaining ballots after the follow-up round, and no shared top count.</summary>
    NoDecisiveResult,

    /// <summary>A bigger change was not unanimous after the follow-up round.</summary>
    NotUnanimous,
}

/// <summary>
/// The tally result. <see cref="Counts"/> holds each voter's final choice, with zero counts for choices nobody picked,
/// for the escalation packet.
/// </summary>
public sealed record TallyDecision(
    TallyDecisionKind Kind,
    string? Choice,
    EscalationReason? Reason,
    IReadOnlyDictionary<string, int> Counts);
