namespace Bazaar.Server.Council;

/// <summary>Sealed first ballot or the single follow-up ballot (docs/design/10-council-and-voting.md, Steps).</summary>
public enum BallotPhase
{
    /// <summary>Written before the reveal, without seeing peers' reasons.</summary>
    Independent,

    /// <summary>Written after the reveal; replaces the voter's independent choice when cast.</summary>
    FollowUp,
}
