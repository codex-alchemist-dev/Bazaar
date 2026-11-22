namespace Bazaar.Server.Council;

/// <summary>
/// Whether a motion is a bigger change (docs/design/10-council-and-voting.md, Membership and role votes).
/// A bigger change passes without the owner only when the vote is unanimous.
/// </summary>
public enum MotionClass
{
    /// <summary>Decided by the vote-type rules (majority of non-abstaining ballots).</summary>
    Ordinary,

    /// <summary>Passes without the owner only when unanimous among eligible voters.</summary>
    BiggerChange,
}
