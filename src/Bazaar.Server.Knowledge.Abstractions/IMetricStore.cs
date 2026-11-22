namespace Bazaar.Server.Knowledge.Metrics;

/// <summary>Where samples are kept. A Postgres implementation replaces the in-memory one once the knowledge component's migrations load.</summary>
public interface IMetricStore
{
    /// <summary>Inserts new samples and replaces the value of an existing sample with the same key. Returns how many rows were new.</summary>
    Task<int> UpsertAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken = default);

    /// <summary>Samples for one project and metric in a date range, inclusive, ordered by bucket.</summary>
    Task<IReadOnlyList<MetricSample>> QueryAsync(string projectId, string metric, DateOnly fromDay, DateOnly toDay, CancellationToken cancellationToken = default);
}
