namespace Bazaar.Server.Council;

/// <summary>Ballot shape of a Council vote (docs/design/10-council-and-voting.md, Vote types).</summary>
public enum VoteKind
{
    /// <summary>Yes, no or abstain.</summary>
    ApproveReject,

    /// <summary>One of 2 to 6 listed options.</summary>
    OptionChoice,
}
