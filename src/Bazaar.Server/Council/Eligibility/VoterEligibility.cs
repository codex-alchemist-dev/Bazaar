namespace Bazaar.Server.Council.Eligibility;

/// <summary>A GC member at vote open, as the eligibility rules see it.</summary>
/// <param name="Id">The member's id.</param>
/// <param name="IsOwner">True for the owner. Owners do not vote unless council.owner_votes is set.</param>
public sealed record GcMember(string Id, bool IsOwner);

/// <summary>The electorate fixed at open.</summary>
/// <param name="Voters">Eligible voters after exclusions. Quorum starts from this set; absence reduces it later.</param>
/// <param name="UnanimityElectorate">The set unanimity is measured over. Kept separate from <paramref name="Voters"/> as doc 10 requires.</param>
public sealed record ElectorateSets(IReadOnlySet<string> Voters, IReadOnlySet<string> UnanimityElectorate);

/// <summary>
/// Pure electorate rules (docs/design/10-council-and-voting.md, Eligibility, quorum and deadlines). No storage or clock.
/// </summary>
/// <remarks>
/// The proposer never votes. The subject's author and, for membership votes, the subject are excluded per the options.
/// The owner is excluded unless council.owner_votes is set. <paramref name="members"/> is the GC's agent members at open;
/// the caller leaves out humans other than the owner.
/// </remarks>
public static class VoterEligibility
{
    /// <summary>Resolves the voters and the unanimity electorate.</summary>
    /// <param name="members">GC members at open. Ids must be distinct.</param>
    /// <param name="proposerId">The agent that opened the vote. Always excluded.</param>
    /// <param name="subjectAuthorId">The subject's author, or null when the subject has none.</param>
    /// <param name="membershipSubjectId">The subject member of a membership vote; null for every other vote.</param>
    /// <param name="options">Exclusion and unanimity settings.</param>
    /// <exception cref="ArgumentException">A member id is duplicated or blank, or the proposer id is blank.</exception>
    public static ElectorateSets Resolve(
        IReadOnlyList<GcMember> members,
        string proposerId,
        string? subjectAuthorId,
        string? membershipSubjectId,
        CouncilEligibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposerId);
        ArgumentNullException.ThrowIfNull(options);

        var everyone = new HashSet<string>(StringComparer.Ordinal);
        var voters = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in members)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(member.Id);
            if (!everyone.Add(member.Id))
            {
                throw new ArgumentException($"GC member '{member.Id}' is listed more than once.", nameof(members));
            }

            if (IsExcluded(member, proposerId, subjectAuthorId, membershipSubjectId, options))
            {
                continue;
            }

            voters.Add(member.Id);
        }

        var unanimity = options.UnanimityElectorate switch
        {
            UnanimityElectorate.EligibleAfterExclusions => new HashSet<string>(voters, StringComparer.Ordinal),
            UnanimityElectorate.AllGcMembers => everyone,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.UnanimityElectorate, null),
        };

        return new ElectorateSets(voters, unanimity);
    }

    private static bool IsExcluded(
        GcMember member,
        string proposerId,
        string? subjectAuthorId,
        string? membershipSubjectId,
        CouncilEligibilityOptions options)
    {
        if (member.Id == proposerId)
        {
            return true;
        }

        if (options.ExcludeAuthor && member.Id == subjectAuthorId)
        {
            return true;
        }

        if (options.ExcludeMembershipSubject && member.Id == membershipSubjectId)
        {
            return true;
        }

        return member.IsOwner && !options.OwnerVotes;
    }
}
