namespace Bazaar.Server.Analytics;

/// <summary>What happened to one unit of an agent's work. Collected from Kanban, review and Git events by whoever owns them.</summary>
public enum AgentWorkEventKind
{
    /// <summary>A task or card the agent worked reached Done.</summary>
    TaskCompleted,

    /// <summary>A review of the agent's change passed.</summary>
    ReviewPassed,

    /// <summary>A review of the agent's change asked for changes.</summary>
    ReviewChangesRequested,

    /// <summary>A pull request the agent opened was merged.</summary>
    PrMerged,

    /// <summary>A merged pull request of the agent was later reverted.</summary>
    PrReverted,
}

/// <summary>One work event for one agent.</summary>
public sealed record AgentWorkEvent(string AgentId, AgentWorkEventKind Kind, DateTimeOffset At);

/// <summary>A half-open time window <c>[From, To)</c>.</summary>
public sealed record AnalyticsWindow(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Length => To - From;

    public bool Contains(DateTimeOffset at) => at >= From && at < To;
}

/// <summary>
/// Metrics for one agent over a window. A rate with no samples is <c>null</c> (a gap, not a zero). There is deliberately no score,
/// rank or money field: these figures describe where the company could help, and feed nothing else (doc 25, Boundaries).
/// </summary>
public sealed record AgentMetrics(
    string AgentId,
    int TasksCompleted,
    double TasksPerWeek,
    int ReviewsPassed,
    int ReviewsTotal,
    double? ReviewPassRate,
    int PrsMerged,
    int PrsReverted,
    double? RevertRate,
    IReadOnlyList<string> CompanyHints);

/// <summary>Company-wide totals for the same window, with the same gap rule.</summary>
public sealed record CompanyMetrics(
    int TasksCompleted,
    double TasksPerWeek,
    double? ReviewPassRate,
    double? RevertRate);

/// <summary>The answer to one analytics read: the window, company totals, and the agents this viewer may see.</summary>
public sealed record AnalyticsReport(AnalyticsWindow Window, CompanyMetrics Company, IReadOnlyList<AgentMetrics> Agents);

/// <summary>Who may read per-agent metrics (setting <c>analytics.agent_metrics_visibility</c>, doc 25). Widening it needs owner approval.</summary>
public enum AnalyticsVisibility
{
    /// <summary>Only the owner.</summary>
    Owner,

    /// <summary>The owner, and each agent its own metrics. The default.</summary>
    OwnerAndSelf,

    /// <summary>The owner and every agent.</summary>
    Peers,
}

/// <summary>The caller, as the host resolved it from the authenticated principal.</summary>
public sealed record AnalyticsViewer(string Id, bool IsOwner, bool IsAgent);
