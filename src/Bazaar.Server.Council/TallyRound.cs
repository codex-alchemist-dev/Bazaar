namespace Bazaar.Server.Council;

/// <summary>
/// Which tally is being taken. The Council allows at most one follow-up round, so there is no third value.
/// </summary>
public enum TallyRound
{
    /// <summary>Tally over independent ballots only (the reveal).</summary>
    Independent,

    /// <summary>Tally after the single follow-up window. Each voter's final choice is their follow-up ballot if cast, else their independent ballot.</summary>
    FollowUp,
}
