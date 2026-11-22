namespace Bazaar.Server.Health;

/// <summary>
/// Metric names the project health module reads from the metric store (doc 25 Health and Analytics). The CI and coverage names
/// live in <c>MetricNames</c> (Knowledge). These are the debt and morale inputs. Every one is a project-level fact; none is
/// scored from agent text (doc 25, Trust and crossing rules: sentiment scoring is not in the signal list).
/// </summary>
public static class HealthMetricNames
{
    /// <summary>Analyzer warnings on the day. Count; the dimension is the toolchain.</summary>
    public const string AnalyzerWarnings = "analyzer.warnings";

    /// <summary>Known-vulnerable packages from the package audit (doc 21). Count.</summary>
    public const string PackageVulnerabilities = "package.vulnerabilities";

    /// <summary>Reverted merges on the day. Count.</summary>
    public const string Reverts = "ci.reverts";

    /// <summary>Share of Kanban cards in Blocked, 0 to 1 (doc 12). Morale signal <see cref="MoraleSignals.BlockedShare"/>.</summary>
    public const string BlockedShare = "kanban.blocked_share";

    /// <summary>Cards moved back out of Done on the day. Count. Morale signal <see cref="MoraleSignals.ReopenedTasks"/>.</summary>
    public const string ReopenedTasks = "kanban.reopened_tasks";

    /// <summary>Council votes decided the other way on a re-vote on the day. Count. Morale signal <see cref="MoraleSignals.VoteReversals"/>.</summary>
    public const string VoteReversals = "gc.vote_reversals";

    /// <summary>Metrics whose daily value is a count and is summed. Every other metric here is a ratio and is averaged.</summary>
    public static bool IsCount(string metric) => metric is not BlockedShare;
}

/// <summary>The morale signal names of the <c>analytics.morale_signals</c> setting (doc 25). Project-level facts only.</summary>
public static class MoraleSignals
{
    public const string BlockedShare = "blocked_share";
    public const string ReopenedTasks = "reopened_tasks";
    public const string VoteReversals = "vote_reversals";

    /// <summary>The shipped default list.</summary>
    public static IReadOnlyList<string> Defaults { get; } = [BlockedShare, ReopenedTasks, VoteReversals];

    /// <summary>The metric that backs a signal, or null for a name this version does not know.</summary>
    public static string? MetricFor(string signal) => signal switch
    {
        BlockedShare => HealthMetricNames.BlockedShare,
        ReopenedTasks => HealthMetricNames.ReopenedTasks,
        VoteReversals => HealthMetricNames.VoteReversals,
        _ => null,
    };
}
