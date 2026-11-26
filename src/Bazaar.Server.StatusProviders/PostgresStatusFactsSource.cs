using Npgsql;

namespace Bazaar.Server.StatusProviders;

/// <summary>Read-only counts over the common migrations. No row content is selected.</summary>
public sealed class PostgresStatusFactsSource(NpgsqlDataSource dataSource) : IStatusFactsSource
{
    private const string _agentsSql = """
        SELECT (SELECT count(*) FROM agent WHERE state <> 'retired'),
               (SELECT count(*) FROM inbox_item WHERE state = 'pending'),
               (SELECT count(*) FROM inbox_item WHERE state = 'claimed'),
               (SELECT count(*) FROM turn WHERE status = 'running'),
               (SELECT count(*) FROM turn WHERE status IN ('failed', 'abandoned') AND ended_at >= now() - interval '15 minutes'),
               COALESCE((SELECT extract(epoch FROM now() - min(due_at))::bigint FROM inbox_item WHERE state = 'pending' AND due_at <= now()), 0)
        """;

    private const string _gatewaySql = """
        SELECT count(*),
               count(*) FILTER (WHERE EXISTS (SELECT 1 FROM gw_quota_state q WHERE q.provider_id = p.id)
                                  AND NOT EXISTS (SELECT 1 FROM gw_quota_state q WHERE q.provider_id = p.id AND q.state = 'available')),
               count(*) FILTER (WHERE EXISTS (SELECT 1 FROM gw_quota_state q WHERE q.provider_id = p.id AND q.state <> 'available'))
        FROM gw_provider p WHERE p.enabled
        """;

    private const string _gcSql = """
        SELECT (SELECT count(*) FROM gc),
               (SELECT count(*) FROM gc_event WHERE created_at >= now() - interval '1 minute')
        """;

    public async Task<AgentFacts> GetAgentsAsync(CancellationToken ct)
    {
        var v = await ReadAsync(_agentsSql, 6, ct).ConfigureAwait(false);
        return new AgentFacts(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    public async Task<GatewayFacts> GetGatewayAsync(CancellationToken ct)
    {
        var v = await ReadAsync(_gatewaySql, 3, ct).ConfigureAwait(false);
        return new GatewayFacts(v[0], v[1], v[2]);
    }

    public async Task<GcFacts> GetGcAsync(CancellationToken ct)
    {
        var v = await ReadAsync(_gcSql, 2, ct).ConfigureAwait(false);
        return new GcFacts(v[0], v[1]);
    }

    private async Task<long[]> ReadAsync(string sql, int columns, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var values = new long[columns];
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < columns; i++)
            {
                values[i] = reader.IsDBNull(i) ? 0 : reader.GetInt64(i);
            }
        }

        return values;
    }
}
