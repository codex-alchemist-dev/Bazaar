namespace Bazaar.Server.Council;

/// <summary>Lifecycle state of a Council vote (docs/design/10-council-and-voting.md, Lifecycle).</summary>
public enum VoteState
{
    /// <summary>Open; sealed independent ballots are accepted.</summary>
    Collecting,

    /// <summary>Independent ballots revealed with the tally and all reasons.</summary>
    Revealed,

    /// <summary>The single follow-up window; follow-up ballots are accepted.</summary>
    FollowUp,

    /// <summary>Terminal. A final choice is recorded.</summary>
    Decided,

    /// <summary>Waiting for the owner to resolve a tie, no quorum, or a bigger-change split.</summary>
    Escalated,

    /// <summary>Terminal. Withdrawn by the proposer or the owner before Decided.</summary>
    Withdrawn,
}
