namespace Bazaar.Server.Council.Eligibility;

/// <summary>
/// Who may vote and whose votes unanimity is measured over (council.unanimity_electorate).
/// </summary>
public enum UnanimityElectorate
{
    /// <summary>Unanimity is measured over the eligible voters after exclusions (the default).</summary>
    EligibleAfterExclusions,

    /// <summary>Unanimity is measured over every GC member, the stricter option.</summary>
    AllGcMembers,
}

/// <summary>
/// Install settings that decide the electorate (docs/design/10-council-and-voting.md, Settings).
/// </summary>
/// <param name="ExcludeAuthor">council.exclude_author. The subject's author cannot vote.</param>
/// <param name="ExcludeMembershipSubject">council.exclude_membership_subject. The subject of a membership vote cannot vote.</param>
/// <param name="OwnerVotes">council.owner_votes. The owner does not vote unless this is true.</param>
/// <param name="UnanimityElectorate">council.unanimity_electorate.</param>
public sealed record CouncilEligibilityOptions(
    bool ExcludeAuthor = true,
    bool ExcludeMembershipSubject = true,
    bool OwnerVotes = false,
    UnanimityElectorate UnanimityElectorate = UnanimityElectorate.EligibleAfterExclusions)
{
    /// <summary>The shipped defaults.</summary>
    public static CouncilEligibilityOptions Defaults { get; } = new();
}

/// <summary>
/// council.vote_deadline_working_days and council.vote_deadline_cap_hours, validated on construction.
/// </summary>
public sealed record VoteDeadlineSettings
{
    /// <summary>Largest cap the settings allow, in hours (three days). Loosening beyond this is rejected.</summary>
    public const int MaxCapHours = 72;

    /// <summary>Creates the settings. Throws <see cref="ArgumentOutOfRangeException"/> outside the allowed ranges.</summary>
    /// <param name="workingDays">council.vote_deadline_working_days. At least 1.</param>
    /// <param name="capHours">council.vote_deadline_cap_hours. Between 1 and <see cref="MaxCapHours"/> inclusive.</param>
    public VoteDeadlineSettings(int workingDays, int capHours)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workingDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capHours, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capHours, MaxCapHours);

        WorkingDays = workingDays;
        CapHours = capHours;
    }

    /// <summary>Working days on each voter's own schedule before the vote closes.</summary>
    public int WorkingDays { get; }

    /// <summary>Wall-clock cap on the vote, in hours from open.</summary>
    public int CapHours { get; }

    /// <summary>The shipped defaults: one working day, capped at 72 hours.</summary>
    public static VoteDeadlineSettings Defaults { get; } = new(1, MaxCapHours);
}

/// <summary>
/// council.vote_extensions_max. The doc fixes one extension, so only 0 and 1 are accepted.
/// </summary>
public sealed record VoteExtensionSettings
{
    /// <summary>Largest number of extensions the doc allows.</summary>
    public const int MaxExtensions = 1;

    /// <summary>Creates the settings. Throws <see cref="ArgumentOutOfRangeException"/> outside 0 to 1.</summary>
    public VoteExtensionSettings(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxExtensions);

        Max = max;
    }

    /// <summary>Extensions a vote may take before a missed quorum escalates.</summary>
    public int Max { get; }

    /// <summary>The shipped default: one extension.</summary>
    public static VoteExtensionSettings Defaults { get; } = new(MaxExtensions);
}
