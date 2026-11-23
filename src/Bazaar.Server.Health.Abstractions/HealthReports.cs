namespace Bazaar.Server.Health;

/// <summary>The kinds of raw report a CI run or an agent pushes for project health (doc 25 sources: coverage, analyzer output, package audit).</summary>
public static class HealthReportKinds
{
    /// <summary>A Cobertura XML coverage report.</summary>
    public const string Coverage = "coverage";

    /// <summary>A SARIF analyzer log.</summary>
    public const string Analyzer = "analyzer";

    /// <summary>The JSON of <c>dotnet list package --vulnerable --format json</c>.</summary>
    public const string PackageAudit = "package_audit";

    public static IReadOnlyList<string> All { get; } = [Coverage, Analyzer, PackageAudit];
}

/// <summary>One raw report for one project and UTC day. A second report with the same key replaces the first.</summary>
/// <param name="Toolchain">Which toolchain produced it (for example <c>dotnet</c>); coverage and analyzer reports are per toolchain.</param>
/// <param name="SourceRef">Where it came from (a CI run URL, a commit sha); shown on the derived samples.</param>
public sealed record HealthReport(string ProjectId, string Kind, string Toolchain, DateOnly Day, string SourceRef, string Content);

/// <summary>Where raw health reports are kept until the raw retention (<c>metrics.raw_retention_days</c>) removes them. The derived samples stay.</summary>
public interface IHealthReportStore
{
    Task PutAsync(HealthReport report, CancellationToken cancellationToken = default);

    /// <summary>Every report for one project and day, ordered by kind, toolchain and source reference.</summary>
    Task<IReadOnlyList<HealthReport>> ListAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>Every project that has at least one report.</summary>
    Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes reports for days before <paramref name="cutoff"/>. Returns how many were removed.</summary>
    Task<int> PruneBeforeAsync(DateOnly cutoff, CancellationToken cancellationToken = default);
}
