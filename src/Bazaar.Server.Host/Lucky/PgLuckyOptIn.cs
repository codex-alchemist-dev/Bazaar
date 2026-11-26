using Bazaar.Lucky;
using Npgsql;

namespace Bazaar.Server.Host.Lucky;

/// <summary>The Postgres <see cref="ILuckyOptInStore"/> on <c>lucky_opt_in</c>.</summary>
public sealed class PgLuckyOptInStore(NpgsqlDataSource dataSource) : ILuckyOptInStore
{
    private const string _sql = """
        INSERT INTO lucky_opt_in (agent_id, opted_in, set_by, updated_at) VALUES ($1, $2, $3, now())
        ON CONFLICT (agent_id) DO UPDATE SET opted_in = EXCLUDED.opted_in, set_by = EXCLUDED.set_by, updated_at = now()
        """;

    /// <inheritdoc />
    public async Task SetAsync(Guid agentId, bool optedIn, Guid setBy, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = optedIn });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = setBy });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>The Postgres <see cref="ILuckyCandidateSource"/>: non-retired agents, active means on shift, opt-in from <c>lucky_opt_in</c>.</summary>
public sealed class PgLuckyCandidateSource(NpgsqlDataSource dataSource) : ILuckyCandidateSource
{
    private const string _sql = """
        SELECT a.agent_id::text, (a.state = 'active') AS on_shift, COALESCE(o.opted_in, false) AS opted_in
        FROM agent AS a
        LEFT JOIN lucky_opt_in AS o ON o.agent_id = a.agent_id
        WHERE a.state <> 'retired'
        ORDER BY a.agent_id
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LuckyCandidate>> ListAsync(CancellationToken ct)
    {
        var list = new List<LuckyCandidate>();
        await using var cmd = dataSource.CreateCommand(_sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new LuckyCandidate(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return list;
    }
}
