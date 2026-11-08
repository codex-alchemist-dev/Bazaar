namespace Bazaar.Server.Council.Service;

/// <summary>Who is counted in unanimity (council.unanimity_electorate).</summary>
public enum UnanimityElectorate
{
    /// <summary>Eligible voters after the exclusions (the default).</summary>
    EligibleAfterExclusions,

    /// <summary>Every GC member. A bigger change then needs all members to vote the same way, so any excluded member blocks it.</summary>
    AllGcMembers,
}

/// <summary>Reason order at the reveal (council.reason_order).</summary>
public enum ReasonOrder
{
    /// <summary>Seeded random order, stored with the vote (the default).</summary>
    Random,

    /// <summary>Sorted by voter id.</summary>
    Fixed,
}

/// <summary>
/// Install settings for the Council service (docs/design/10-council-and-voting.md, Settings). Defaults are the doc's.
/// Settings may only tighten. The one follow-up round and sealed ballots are not options at all: the service
/// always runs at most one follow-up round and never shows a ballot before the reveal.
/// </summary>
public sealed record CouncilOptions
{
    /// <summary>council.quorum_min: ballots needed for quorum, at least 1.</summary>
    public int QuorumMin { get; init; } = Tally.DefaultQuorumMin;

    /// <summary>council.vote_extensions_max: deadline extensions granted before escalation.</summary>
    public int VoteExtensionsMax { get; init; } = 1;

    /// <summary>council.vote_deadline_cap_hours: wall-clock cap on each deadline window, from open or extension.</summary>
    public int VoteDeadlineCapHours { get; init; } = 72;

    /// <summary>
    /// council.open_votes_per_agent_day. Carried for the configuration layer; this service does not enforce it because the
    /// store port has no per-agent count.
    /// </summary>
    public int OpenVotesPerAgentDay { get; init; } = 5;

    /// <summary>
    /// council.reopen_cooldown_hours. Carried for the configuration layer; this service does not enforce it because the
    /// store port has no subject history.
    /// </summary>
    public int ReopenCooldownHours { get; init; } = 24;

    /// <summary>council.unanimity_electorate.</summary>
    public UnanimityElectorate UnanimityElectorate { get; init; } = UnanimityElectorate.EligibleAfterExclusions;

    /// <summary>council.exclude_author: the subject's author cannot vote.</summary>
    public bool ExcludeAuthor { get; init; } = true;

    /// <summary>council.exclude_membership_subject: the agent a membership vote is about cannot vote.</summary>
    public bool ExcludeMembershipSubject { get; init; } = true;

    /// <summary>council.owner_votes: the owner is in the electorate.</summary>
    public bool OwnerVotes { get; init; }

    /// <summary>council.followup_enabled: when off, a non-decisive independent tally escalates without a follow-up round.</summary>
    public bool FollowupEnabled { get; init; } = true;

    /// <summary>council.reason_min_sentences: minimum sentence-ending marks (. ! ?) in a ballot's reasoning.</summary>
    public int ReasonMinSentences { get; init; } = 2;

    /// <summary>council.reason_max_chars: maximum length of a ballot's or owner's reasoning.</summary>
    public int ReasonMaxChars { get; init; } = 2000;

    /// <summary>council.reason_order.</summary>
    public ReasonOrder ReasonOrder { get; init; } = ReasonOrder.Random;

    /// <summary>Checks the values. Throws when any is out of range.</summary>
    /// <exception cref="ArgumentException">One or more settings are out of range.</exception>
    public void Validate()
    {
        var problems = new List<string>();
        if (QuorumMin < 1)
        {
            problems.Add("council.quorum_min must be at least 1.");
        }

        if (VoteExtensionsMax < 0)
        {
            problems.Add("council.vote_extensions_max must not be negative.");
        }

        if (VoteDeadlineCapHours < 1)
        {
            problems.Add("council.vote_deadline_cap_hours must be at least 1.");
        }

        if (OpenVotesPerAgentDay < 1)
        {
            problems.Add("council.open_votes_per_agent_day must be at least 1.");
        }

        if (ReopenCooldownHours < 0)
        {
            problems.Add("council.reopen_cooldown_hours must not be negative.");
        }

        if (ReasonMinSentences < 0)
        {
            problems.Add("council.reason_min_sentences must not be negative.");
        }

        if (ReasonMaxChars < 1)
        {
            problems.Add("council.reason_max_chars must be at least 1.");
        }

        if (!Enum.IsDefined(UnanimityElectorate))
        {
            problems.Add("council.unanimity_electorate is not a known value.");
        }

        if (!Enum.IsDefined(ReasonOrder))
        {
            problems.Add("council.reason_order is not a known value.");
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(CouncilOptions));
        }
    }
}
