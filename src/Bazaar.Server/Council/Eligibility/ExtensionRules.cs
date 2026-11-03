namespace Bazaar.Server.Council.Eligibility;

/// <summary>
/// Extension rule at a deadline (docs/design/10-council-and-voting.md, Eligibility, quorum and deadlines). A thin wrapper over
/// <see cref="VoteStateMachine.DeadlineEvent"/> with council.vote_extensions_max. Pure.
/// </summary>
public static class ExtensionRules
{
    /// <summary>
    /// The event for a deadline. With quorum the vote reveals. Without quorum the vote is extended while extensions
    /// remain; once they are used up, the next missed deadline escalates the vote.
    /// </summary>
    /// <param name="quorumMet">Whether quorum was met at the deadline.</param>
    /// <param name="extensionsUsed">Extensions already taken on the vote.</param>
    /// <param name="settings">council.vote_extensions_max.</param>
    public static VoteEvent DeadlineEvent(bool quorumMet, int extensionsUsed, VoteExtensionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(extensionsUsed);

        return VoteStateMachine.DeadlineEvent(quorumMet, extensionsUsed, settings.Max);
    }
}
