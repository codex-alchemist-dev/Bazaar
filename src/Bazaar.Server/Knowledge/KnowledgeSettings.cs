using Bazaar.Server.Settings;

namespace Bazaar.Server.Knowledge;

/// <summary>Who sees per-agent metrics (doc 25 <c>analytics.agent_metrics_visibility</c>).</summary>
public enum AgentMetricsVisibility
{
    Owner,
    OwnerPlusSelf,
    Peers,
}

/// <summary>Project-level morale signals computed from facts, never from agent sentiment (doc 25 Conflicts 2).</summary>
public enum MoraleSignal
{
    BlockedShare,
    ReopenedTasks,
    VoteReversals,
}

/// <summary>When the Archive writes a chapter (doc 25 <c>archive.chapter_cadence</c>).</summary>
public enum ChapterCadence
{
    ReleaseTag,
    Monthly,
    OnDemand,
}

/// <summary>Motion classes that go to the owner unless unanimous (doc 25 <c>council.bigger_change_classes</c>, shared with 07 and 10).</summary>
public enum BiggerChangeClass
{
    NewAgent,
    ToolGrant,
    Promotion,
    Retirement,
    ModelWhitelistChange,
}

/// <summary>Who reviews Genetic Memory Transfer candidates (doc 25 <c>gmt.lesson_reviewer</c>).</summary>
public enum LessonReviewer
{
    Architect,
    Owner,
}

/// <summary>
/// The knowledge-area settings from doc 25 ("MOVED TO SETTINGS" and the settings table). Defaults match the table.
/// Keys that the table marks as needing approval carry Tier 2 and, where the table says owner, the owner role.
/// Changes are audited like every setting. <c>hive.grants</c> and <c>hive.allowed_kinds</c> belong to doc 08, not here.
/// </summary>
/// <remarks>
/// TODO(component): registered in <see cref="SettingRegistry.Known"/> until the component model (PR #125) moves registration
/// to the knowledge component's own AddBazaarKnowledge().
/// </remarks>
public static class KnowledgeSettings
{
    public static readonly SettingKey<int> HiveGrantReviewDays = new(
        "hive.grant_review_days", 30, SettingScope.Install, SettingRole.Owner,
        "Days a nightly batch of Hive lessons waits for review before it is copied automatically. 0 keeps every batch under review.",
        ApprovalTier.Tier2, validate: Between(0, 365));

    public static readonly SettingKey<AgentMetricsVisibility> AgentMetricsVisibilityKey = new(
        "analytics.agent_metrics_visibility", AgentMetricsVisibility.OwnerPlusSelf, SettingScope.Install, SettingRole.Operator,
        "Who may see per-agent metrics: the owner, the owner and the agent itself, or peers. Widening needs approval.",
        ApprovalTier.Tier2, requiresApprovalFor: value => value == AgentMetricsVisibility.Peers);

    public static readonly SettingKey<IReadOnlyList<MoraleSignal>> MoraleSignalsKey = new(
        "analytics.morale_signals", [MoraleSignal.BlockedShare, MoraleSignal.ReopenedTasks, MoraleSignal.VoteReversals],
        SettingScope.Install, SettingRole.Operator,
        "Project-level signals shown as morale. Each is a count or share from recorded facts.");

    public static readonly SettingKey<int> HealthCoverageDropPoints = new(
        "health.coverage_drop_points", 2, SettingScope.Project, SettingRole.Owner,
        "Coverage drop, in percentage points, that raises an alert. Compared only within one toolchain.",
        validate: Between(0, 100));

    public static readonly SettingKey<int> HealthBuildFailureStreak = new(
        "health.build_failure_streak", 3, SettingScope.Project, SettingRole.Owner,
        "Consecutive failed builds that raise an alert.",
        validate: Between(1, 100));

    public static readonly SettingKey<IReadOnlyList<ChapterCadence>> ArchiveChapterCadenceKey = new(
        "archive.chapter_cadence", [ChapterCadence.ReleaseTag, ChapterCadence.Monthly], SettingScope.Project, SettingRole.Owner,
        "When Archive chapters are written: at each release tag, monthly, or on demand.");

    public static readonly SettingKey<bool> ArchiveIncludeHumanText = new(
        "archive.include_human_text", false, SettingScope.Project, SettingRole.Owner,
        "Include human messages in the Archive, opted in per GC. Needs counsel review.",
        ApprovalTier.Tier2);

    public static readonly SettingKey<bool> ArchiveIncludeCouncilReasons = new(
        "archive.include_council_reasons", true, SettingScope.Project, SettingRole.Owner,
        "Include Council reasons in the Archive. Reasons appear only after the joint reveal; that rule is a floor enforced by the Archive generator.");

    public static readonly SettingKey<IReadOnlyList<BiggerChangeClass>> CouncilBiggerChangeClassesKey = new(
        "council.bigger_change_classes", [BiggerChangeClass.NewAgent, BiggerChangeClass.ToolGrant], SettingScope.Install, SettingRole.Operator,
        "Motion classes that need a unanimous vote or the owner. Council may propose changes; the owner approves them.",
        ApprovalTier.Tier2);

    public static readonly SettingKey<LessonReviewer> GmtLessonReviewerKey = new(
        "gmt.lesson_reviewer", LessonReviewer.Architect, SettingScope.Install, SettingRole.Operator,
        "Who reviews Genetic Memory Transfer lesson candidates. Tool grants and charter text always go to the owner.",
        ApprovalTier.Tier2);

    public static readonly SettingKey<int> MetricsRawRetentionDays = new(
        "metrics.raw_retention_days", 90, SettingScope.Install, SettingRole.Operator,
        "Days raw metric samples are kept before rollups replace them. Needs counsel review.",
        ApprovalTier.Tier2, validate: Between(1, 3650));

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        HiveGrantReviewDays,
        AgentMetricsVisibilityKey,
        MoraleSignalsKey,
        HealthCoverageDropPoints,
        HealthBuildFailureStreak,
        ArchiveChapterCadenceKey,
        ArchiveIncludeHumanText,
        ArchiveIncludeCouncilReasons,
        CouncilBiggerChangeClassesKey,
        GmtLessonReviewerKey,
        MetricsRawRetentionDays,
    ];

    private static Func<int, string?> Between(int min, int max) =>
        value => value >= min && value <= max ? null : $"must be from {min} to {max}; got {value}.";
}
