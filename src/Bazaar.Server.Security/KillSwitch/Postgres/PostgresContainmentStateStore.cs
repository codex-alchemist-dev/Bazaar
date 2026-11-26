using Npgsql;

namespace Bazaar.Server.Security.KillSwitch.Postgres;

/// <summary>
/// Postgres <see cref="IContainmentStateStore"/> over the <c>agent_containment</c> table (agent_safety migration 0003). Every
/// instant is the caller's instant, never the database clock. Works under the runtime role (<c>bazaar_app</c>): SELECT, INSERT
/// and DELETE only.
/// </summary>
/// <remarks>
/// <see cref="AddAsync"/> uses <c>ON CONFLICT DO NOTHING</c>, so a repeated contain keeps the first record and needs no UPDATE
/// privilege. Instants are cut to microseconds, the precision Postgres keeps.
/// </remarks>
public sealed class PostgresContainmentStateStore(NpgsqlDataSource dataSource) : IContainmentStateStore
{
    private const string _load = "SELECT agent_id, contained_at, actor_id FROM agent_containment ORDER BY agent_id";
    private const string _add =
        "INSERT INTO agent_containment (agent_id, contained_at, actor_id) VALUES ($1, $2, $3) ON CONFLICT (agent_id) DO NOTHING";
    private const string _listIds = "SELECT agent_id FROM agent_containment";
    private const string _remove = "DELETE FROM agent_containment WHERE agent_id = $1";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_load);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // Rows written before ids were canonicalised may carry another spelling; two spellings of one agent keep the earlier record.
        var byAgent = new SortedDictionary<string, ContainedAgent>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = AgentIds.Canonical(reader.GetString(0));
            var agent = new ContainedAgent(key, reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2));
            if (!byAgent.TryGetValue(key, out var kept) || agent.ContainedAt < kept.ContainedAt)
            {
                byAgent[key] = agent;
            }
        }
        return byAgent.Values.ToArray();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><see cref="ContainedAgent.AgentId"/> is null, empty or whitespace.</exception>
    public async Task AddAsync(ContainedAgent agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentId);
        ct.ThrowIfCancellationRequested();

        var canonical = AgentIds.Canonical(agent.AgentId);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // An agent already stored under any spelling keeps its first record.
        if ((await FindSpellingsAsync(conn, tx, canonical, ct).ConfigureAwait(false)).Count > 0)
        {
            return;
        }

        await using var cmd = new NpgsqlCommand(_add, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = canonical });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(agent.ContainedAt) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agent.ActorId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    public async Task RemoveAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var stored in await FindSpellingsAsync(conn, tx, AgentIds.Canonical(agentId), ct).ConfigureAwait(false))
        {
            await using var cmd = new NpgsqlCommand(_remove, conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = stored });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every stored spelling of one agent. The ids are compared in code (not in SQL), so rows written before ids were canonicalised
    /// match too and no migration is needed. The table holds one row per contained agent.
    /// </summary>
    private static async Task<List<string>> FindSpellingsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string canonical, CancellationToken ct)
    {
        var spellings = new List<string>();
        await using var cmd = new NpgsqlCommand(_listIds, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var stored = reader.GetString(0);
            if (string.Equals(AgentIds.Canonical(stored), canonical, StringComparison.Ordinal))
            {
                spellings.Add(stored);
            }
        }
        return spellings;
    }

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
