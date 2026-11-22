using Bazaar.Server.Knowledge.Metrics;
using Npgsql;

namespace Bazaar.Server.Health;

/// <summary>
/// Postgres metric store (<c>metric_samples</c>, migration components/health/0001, doc 25). Same behaviour as
/// <see cref="InMemoryMetricStore"/>: an existing sample with the same key has its value replaced, and a query returns one project
/// and metric over an inclusive date range, ordered by bucket, then dimension, then source reference.
/// </summary>
public sealed class PgMetricStore(NpgsqlDataSource dataSource) : IMetricStore
{
    public async Task<int> UpsertAsync(IReadOnlyList<MetricSample> samples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var added = 0;
        foreach (var sample in samples)
        {
            // xmax is 0 for a freshly inserted row and non-zero for one the conflict clause updated.
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO metric_samples (project_id, metric, dimension, bucket, value, source_ref)
                VALUES (@project, @metric, @dimension, @bucket, @value, @source)
                ON CONFLICT (project_id, metric, dimension, bucket, source_ref)
                DO UPDATE SET value = EXCLUDED.value, recorded_at = now()
                RETURNING (xmax = 0)
                """, connection, tx);
            cmd.Parameters.AddWithValue("project", sample.ProjectId);
            cmd.Parameters.AddWithValue("metric", sample.Metric);
            cmd.Parameters.AddWithValue("dimension", sample.Dimension);
            cmd.Parameters.AddWithValue("bucket", sample.Bucket);
            cmd.Parameters.AddWithValue("value", sample.Value);
            cmd.Parameters.AddWithValue("source", sample.SourceRef);
            if ((bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                added++;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    public async Task<IReadOnlyList<MetricSample>> QueryAsync(string projectId, string metric, DateOnly fromDay, DateOnly toDay, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        await using var cmd = dataSource.CreateCommand("""
            SELECT project_id, metric, dimension, bucket, value, source_ref
            FROM metric_samples
            WHERE project_id = @project AND metric = @metric AND bucket >= @from AND bucket <= @to
            ORDER BY bucket, dimension COLLATE "C", source_ref COLLATE "C"
            """);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("metric", metric);
        cmd.Parameters.AddWithValue("from", fromDay);
        cmd.Parameters.AddWithValue("to", toDay);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<MetricSample>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MetricSample(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateOnly.FromDateTime(reader.GetDateTime(3)), reader.GetDouble(4), reader.GetString(5)));
        }

        return rows;
    }
}
