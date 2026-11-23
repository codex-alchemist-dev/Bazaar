namespace Bazaar.Server.Health;

/// <summary>In-memory <see cref="IHealthReportStore"/> for hosts without Postgres and for tests. Same replace-on-same-key behaviour as the Postgres store.</summary>
public sealed class InMemoryHealthReportStore : IHealthReportStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Project, string Kind, string Toolchain, DateOnly Day, string Source), HealthReport> _reports = [];

    public Task PutAsync(HealthReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
            _reports[(report.ProjectId, report.Kind, report.Toolchain, report.Day, report.SourceRef)] = report;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HealthReport>> ListAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<HealthReport>>([.. _reports.Values
                .Where(r => r.ProjectId == projectId && r.Day == day)
                .OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Toolchain, StringComparer.Ordinal).ThenBy(r => r.SourceRef, StringComparer.Ordinal)]);
    }

    public Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<string>>([.. _reports.Values.Select(r => r.ProjectId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    public Task<int> PruneBeforeAsync(DateOnly cutoff, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var old = _reports.Where(p => p.Value.Day < cutoff).Select(p => p.Key).ToList();
            foreach (var key in old)
                _reports.Remove(key);
            return Task.FromResult(old.Count);
        }
    }
}
