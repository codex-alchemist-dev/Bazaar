using Bazaar.Server.Council.Service;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Council.Settings;

/// <summary>
/// The <c>council.*</c> keys of docs/design/10-council-and-voting.md (Settings table), registered through
/// <see cref="CouncilSettingContribution"/>. All are install scope and operator-changed; the keys the table marks "Yes, audited" for
/// approval carry Tier 2. Defaults and bounds are the doc's; settings may tighten, never loosen below a DECIDED floor, and a
/// validator is where each floor lives, so a value below it is rejected however it arrives.
/// </summary>
/// <remarks>
/// Two keys of the table are not declared here because another module already registers them and the registry rejects a name twice:
/// <c>council.bigger_change_classes</c> and <c>council.followup_dissent_share</c> are in <c>KnowledgeSettings</c> (Bazaar.Server).
/// <see cref="Names"/> lists all 23 so a test can prove none is missing from the combined registry.
/// </remarks>
public static class CouncilSettings
{
    private const string _bigger = "council.bigger_change_classes";
    private const string _dissent = "council.followup_dissent_share";

    /// <summary>Doc 10 keys declared elsewhere (<c>KnowledgeSettings</c>), so this module does not register them again.</summary>
    public static IReadOnlyList<string> RegisteredElsewhere { get; } = [_bigger, _dissent];

    public static readonly SettingKey<decimal> ReputationWeight = new(
        "council.reputation_weight", 1.0m, SettingScope.Install, SettingRole.Operator,
        "Weight bound for voters with a strong record on hard outcomes. 1.0 means equal weight; at most 1.5.",
        ApprovalTier.Tier2, validate: v => v is >= 1.0m and <= 1.5m ? null : $"must be from 1.0 to 1.5; got {v}.");

    public static readonly SettingKey<bool> OwnerVotes = new(
        "council.owner_votes", false, SettingScope.Install, SettingRole.Operator,
        "Whether the owner is in the electorate.", ApprovalTier.Tier2);

    public static readonly SettingKey<bool> ExcludeAuthor = new(
        "council.exclude_author", true, SettingScope.Install, SettingRole.Operator,
        "Whether the author of a motion's subject is kept out of the electorate.", ApprovalTier.Tier2);

    public static readonly SettingKey<bool> ExcludeMembershipSubject = new(
        "council.exclude_membership_subject", true, SettingScope.Install, SettingRole.Operator,
        "Whether the agent a membership vote is about is kept out of the electorate.", ApprovalTier.Tier2);

    public static readonly SettingKey<UnanimityElectorate> UnanimityElectorateKey = new(
        "council.unanimity_electorate", UnanimityElectorate.EligibleAfterExclusions, SettingScope.Install, SettingRole.Operator,
        "Who is counted for unanimity: eligible voters after the exclusions, or every GC member.", ApprovalTier.Tier2,
        validate: v => Enum.IsDefined(v) ? null : "is not a known value.");

    public static readonly SettingKey<int> QuorumMin = new(
        "council.quorum_min", Tally.DefaultQuorumMin, SettingScope.Install, SettingRole.Operator,
        "Ballots needed for quorum. At least 1.", validate: AtLeast(1));

    public static readonly SettingKey<bool> AbstainCountsQuorum = new(
        "council.abstain_counts_quorum", true, SettingScope.Install, SettingRole.Operator,
        "Whether abstentions count toward quorum. They never count toward the threshold.");

    public static readonly SettingKey<IReadOnlyList<VoteKind>> VoteTypesEnabled = new(
        "council.vote_types_enabled", [VoteKind.ApproveReject, VoteKind.OptionChoice], SettingScope.Install, SettingRole.Operator,
        "Vote types a motion may use: approve_reject and option_choice. Ranked is not built.",
        validate: CheckVoteTypes);

    public static readonly SettingKey<int> VoteDeadlineWorkingDays = new(
        "council.vote_deadline_working_days", 1, SettingScope.Install, SettingRole.Operator,
        "Working days a voter has, on the voter's own schedule, before the cap in vote_deadline_cap_hours. At least 1 [PROPOSED bound].",
        validate: AtLeast(1));

    public static readonly SettingKey<int> VoteDeadlineCapHours = new(
        "council.vote_deadline_cap_hours", 72, SettingScope.Install, SettingRole.Operator,
        "Wall-clock cap, in hours, on each deadline window. At least 1.", validate: AtLeast(1));

    public static readonly SettingKey<int> VoteExtensionsMax = new(
        "council.vote_extensions_max", 1, SettingScope.Install, SettingRole.Operator,
        "Deadline extensions granted before the vote escalates. 0 or more.", validate: AtLeast(0));

    public static readonly SettingKey<bool> FollowupEnabled = new(
        "council.followup_enabled", true, SettingScope.Install, SettingRole.Operator,
        "Whether a non-decisive independent tally goes to the one follow-up round (on in the pilot). The one-round cap is fixed.",
        ApprovalTier.Tier2);

    public static readonly SettingKey<int> ReasonMinSentences = new(
        "council.reason_min_sentences", 2, SettingScope.Install, SettingRole.Operator,
        "Minimum sentence-ending marks in a ballot's reasoning. 0 or more.", validate: AtLeast(0));

    public static readonly SettingKey<int> ReasonMaxChars = new(
        "council.reason_max_chars", 2000, SettingScope.Install, SettingRole.Operator,
        "Maximum length of a ballot's or owner's reasoning. At least 1.", validate: AtLeast(1));

    public static readonly SettingKey<ReasonOrder> ReasonOrderKey = new(
        "council.reason_order", ReasonOrder.Random, SettingScope.Install, SettingRole.Operator,
        "Order of reasons at the reveal: random (seeded, stored) or fixed (by voter id).",
        validate: v => Enum.IsDefined(v) ? null : "is not a known value.");

    public static readonly SettingKey<int> EscalationRenotifyHours = new(
        "council.escalation_renotify_hours", 4, SettingScope.Install, SettingRole.Operator,
        "Hours between owner re-notifications on an escalated vote. At least 1 [PROPOSED bound].", validate: AtLeast(1));

    public static readonly SettingKey<int> OpenVotesPerAgentDay = new(
        "council.open_votes_per_agent_day", 5, SettingScope.Install, SettingRole.Operator,
        "Votes one agent may open per day. At least 1.", validate: AtLeast(1));

    public static readonly SettingKey<int> ReopenCooldownHours = new(
        "council.reopen_cooldown_hours", 24, SettingScope.Install, SettingRole.Operator,
        "Hours before a withdrawn or decided subject may be reopened without new evidence. 0 or more.", validate: AtLeast(0));

    public static readonly SettingKey<decimal> PilotHoldoutShare = new(
        "council.pilot_holdout_share", 0.15m, SettingScope.Install, SettingRole.Operator,
        "Share of votes that skip follow-up during the pilot. From 0 to 1.", validate: Share);

    public static readonly SettingKey<decimal> PilotStopFlipShare = new(
        "council.pilot_stop_flip_share", 0.20m, SettingScope.Install, SettingRole.Operator,
        "Share of evidence-free flips above which follow-up stops. From 0 to 1.", validate: Share);

    public static readonly SettingKey<int> PilotTargetVotes = new(
        "council.pilot_target_votes", 20, SettingScope.Install, SettingRole.Operator,
        "Votes needed before the pilot stop rule is evaluated. At least 1.", validate: AtLeast(1));

    /// <summary>The keys this module registers (21 of the doc's 23; see the class remarks).</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        ReputationWeight, OwnerVotes, ExcludeAuthor, ExcludeMembershipSubject, UnanimityElectorateKey, QuorumMin,
        AbstainCountsQuorum, VoteTypesEnabled, VoteDeadlineWorkingDays, VoteDeadlineCapHours, VoteExtensionsMax,
        FollowupEnabled, ReasonMinSentences, ReasonMaxChars, ReasonOrderKey, EscalationRenotifyHours,
        OpenVotesPerAgentDay, ReopenCooldownHours, PilotHoldoutShare, PilotStopFlipShare, PilotTargetVotes,
    ];

    /// <summary>Every <c>council.*</c> name of doc 10's Settings table: this module's keys plus <see cref="RegisteredElsewhere"/>.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(k => k.Name), .. RegisteredElsewhere];

    private static Func<int, string?> AtLeast(int min) => v => v >= min ? null : $"must be at least {min}; got {v}.";

    private static string? Share(decimal v) => v is >= 0m and <= 1m ? null : $"must be from 0 to 1; got {v}.";

    private static string? CheckVoteTypes(IReadOnlyList<VoteKind> kinds)
    {
        if (kinds.Count == 0)
        {
            return "must enable at least one vote type [PROPOSED bound].";
        }

        if (kinds.Any(k => !Enum.IsDefined(k)))
        {
            return "holds an unknown vote type.";
        }

        return kinds.Distinct().Count() == kinds.Count ? null : "lists a vote type twice.";
    }
}

/// <summary>The Council module's contribution to the settings registry. <see cref="CouncilComponent"/> registers it.</summary>
public sealed class CouncilSettingContribution : ISettingContribution
{
    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> Settings => CouncilSettings.All;
}
