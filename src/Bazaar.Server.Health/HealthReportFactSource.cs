namespace Bazaar.Server.Health;

/// <summary>
/// Turns the raw reports pushed for a project and day into health facts (doc 25: coverage, analyzer output, package audit). Coverage
/// and analyzer reports stay per toolchain. Only one package audit is read per day: the first by toolchain and source order, because
/// <see cref="HealthFacts.PackageAudit"/> is a single value. No report for a day means no fact, which is a gap, never a zero.
/// </summary>
public sealed class HealthReportFactSource(IHealthReportStore reports) : IHealthFactSource, IHealthProjectSource
{
    public async Task<HealthFacts> ReadAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default)
    {
        var list = await reports.ListAsync(projectId, day, cancellationToken).ConfigureAwait(false);
        var audit = list.FirstOrDefault(r => r.Kind == HealthReportKinds.PackageAudit);
        return new HealthFacts
        {
            Coverage = [.. list.Where(r => r.Kind == HealthReportKinds.Coverage).Select(r => new CoverageReportFact(r.Toolchain, r.Content, r.SourceRef))],
            Analyzer = [.. list.Where(r => r.Kind == HealthReportKinds.Analyzer).Select(r => new AnalyzerReportFact(r.Toolchain, r.Content, r.SourceRef))],
            PackageAudit = audit is null ? null : new PackageAuditFact(audit.Content, audit.SourceRef),
        };
    }

    public Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        reports.ListProjectsAsync(cancellationToken);
}
