namespace Bazaar.Server.Health;

/// <summary>Overall state of a project's health.</summary>
public enum ProjectHealthStatus
{
    /// <summary>Data exists and no alert fired.</summary>
    Healthy,

    /// <summary>At least one alert fired.</summary>
    Alert,

    /// <summary>Nothing is recorded in the window, so nothing can be judged. A gap, never a pass.</summary>
    NoData,
}

/// <summary>Which way a value moved between the two newest recorded days.</summary>
public enum TrendDirection
{
    /// <summary>Only one recorded day, so no direction.</summary>
    Unknown,
    Rising,
    Flat,
    Falling,
}

/// <summary>A value on its newest recorded day, with the one before it. A day with no sample is a gap and is skipped, never read as zero.</summary>
public sealed record MetricTrend(double Latest, DateOnly LatestDay, double? Previous)
{
    /// <summary>Latest minus previous, or null with a single recorded day.</summary>
    public double? Change => Previous is null ? null : Math.Round(Latest - Previous.Value, 4);

    public TrendDirection Direction => Change switch
    {
        null => TrendDirection.Unknown,
        > 0 => TrendDirection.Rising,
        < 0 => TrendDirection.Falling,
        _ => TrendDirection.Flat,
    };
}

/// <summary>Latest line coverage for one toolchain. Coverage is only ever compared inside one toolchain.</summary>
public sealed record CoverageReading(string Toolchain, MetricTrend Trend);

/// <summary>CI results over the window. <see cref="PassRate"/> is null when no run was recorded.</summary>
public sealed record BuildReading(int Runs, int Passed, double? PassRate);

/// <summary>Tech debt figures. Each is null when nothing was recorded; there is no composite score (doc 25 defines none).</summary>
public sealed record TechDebtReading(MetricTrend? AnalyzerWarnings, MetricTrend? PackageVulnerabilities, double? RevertsInWindow);

/// <summary>One enabled morale signal (doc 25 <c>analytics.morale_signals</c>), with its trend when data exists.</summary>
public sealed record MoraleReading(string Signal, MetricTrend? Trend, double? WindowTotal);

/// <summary>One alert. <see cref="Kind"/> is for code (<c>coverage_drop</c>, <c>build_failure_streak</c>); <see cref="Message"/> is for people.</summary>
public sealed record HealthAlert(string Kind, string Message);

/// <summary>The project health view the endpoint and the dashboard card show.</summary>
public sealed record ProjectHealthReport(
    string ProjectId,
    DateOnly AsOf,
    int WindowDays,
    ProjectHealthStatus Status,
    IReadOnlyList<HealthAlert> Alerts,
    IReadOnlyList<CoverageReading> Coverage,
    BuildReading Build,
    TechDebtReading TechDebt,
    IReadOnlyList<MoraleReading> Morale);

/// <summary>
/// Per-project health settings (doc 25: <c>health.coverage_drop_points</c>, <c>health.build_failure_streak</c>,
/// <c>analytics.morale_signals</c>). The Settings service feeds these once it lands; until then <c>Bazaar:Health</c> configuration does.
/// </summary>
public sealed record ProjectHealthSettings
{
    /// <summary>Shipped defaults: 2 points, 3 days, 30-day window, the three default morale signals.</summary>
    public static ProjectHealthSettings Defaults { get; } = new();

    public int CoverageDropPoints { get; init; } = 2;

    public int BuildFailureStreak { get; init; } = 3;

    public int WindowDays { get; init; } = 30;

    public IReadOnlyList<string> MoraleSignals { get; init; } = Bazaar.Server.Health.MoraleSignals.Defaults;
}

/// <summary>Builds the project health report from stored metric samples. Read-only: it never writes metrics, reputation or schedules.</summary>
public interface IProjectHealthService
{
    Task<ProjectHealthReport> GetAsync(string projectId, CancellationToken cancellationToken = default);
}
