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
    private const string _remove = "DELETE FROM agent_containment WHERE agent_id = $1";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_load);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var agents = new List<ContainedAgent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            agents.Add(new ContainedAgent(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetString(2)));
        }
        return agents;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><see cref="ContainedAgent.AgentId"/> is null, empty or whitespace.</exception>
    public async Task AddAsync(ContainedAgent agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.AgentId);
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_add);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agent.AgentId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(agent.ContainedAt) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agent.ActorId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    public async Task RemoveAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_remove);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
