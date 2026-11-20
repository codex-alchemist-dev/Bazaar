namespace Bazaar.Server.Knowledge.Metrics;

/// <summary>Test and development store. Thread-safe per instance.</summary>
public sealed class InMemoryMetricStore : IMetricStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MetricSample> _rows = new(StringComparer.Ordinal);

    public Task<int> UpsertAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        cancellationToken.ThrowIfCancellationRequested();

        var added = 0;
        lock (_gate)
        {
            foreach (var sample in samples)
            {
                if (!_rows.ContainsKey(sample.Key))
                    added++;
                _rows[sample.Key] = sample;
            }
        }

        return Task.FromResult(added);
    }

    public Task<IReadOnlyList<MetricSample>> QueryAsync(string projectId, string metric, DateOnly fromDay, DateOnly toDay, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IReadOnlyList<MetricSample> rows = _rows.Values
                .Where(s => s.ProjectId == projectId && s.Metric == metric && s.Bucket >= fromDay && s.Bucket <= toDay)
                .OrderBy(s => s.Bucket)
                .ThenBy(s => s.Dimension, StringComparer.Ordinal)
                .ThenBy(s => s.SourceRef, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(rows);
        }
    }
}
