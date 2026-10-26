namespace Bazaar.Server.Council;

/// <summary>Events that move a Council vote between states (docs/design/10-council-and-voting.md, Lifecycle).</summary>
public enum VoteEvent
{
    /// <summary>Every eligible voter has cast an independent ballot.</summary>
    AllEligibleCast,

    /// <summary>The deadline passed with quorum met.</summary>
    DeadlineWithQuorum,

    /// <summary>The deadline passed without quorum and an extension is available; the deadline moves.</summary>
    DeadlineExtended,

    /// <summary>The deadline passed without quorum and no extension is left.</summary>
    DeadlineWithoutQuorum,

    /// <summary>The tally is decisive.</summary>
    Decide,

    /// <summary>The tally is not decisive; run the single follow-up round.</summary>
    RequireFollowUp,

    /// <summary>The tally goes to the owner (tie, no quorum, no decisive result, or a bigger-change split).</summary>
    Escalate,

    /// <summary>The owner picked a choice for an escalated vote.</summary>
    OwnerResolved,

    /// <summary>The proposer or the owner withdrew the motion.</summary>
    Withdraw,
}
