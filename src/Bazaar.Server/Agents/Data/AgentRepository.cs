using Npgsql;

namespace Bazaar.Server.Agents.Data;

/// <summary>SQL-first access to the <c>agent</c> table (doc 06, "Lifecycle").</summary>
public sealed class AgentRepository(NpgsqlDataSource dataSource)
{
    /// <summary>Creates an agent in <see cref="AgentState.Created"/>. Returns its id.</summary>
    public async Task<Guid> CreateAsync(string name, Guid? soulVersionId = null, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO agent (name, soul_version_id, state)
            VALUES (@name, @soul_version_id, 'created')
            RETURNING agent_id
            """);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("soul_version_id", AgentSql.Nullable(soulVersionId));
        return (Guid)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<AgentRecord?> GetAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT agent_id, name, soul_version_id, state, created_at FROM agent WHERE agent_id = @agent_id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new AgentRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            AgentSql.FromDb<AgentState>(reader.GetString(3)),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    /// <summary>
    /// Moves the agent from <paramref name="from"/> to <paramref name="to"/> only if it is still in <paramref name="from"/>.
    /// Returns false when the state changed underneath the caller. Throws when the move is not in the lifecycle.
    /// </summary>
    public async Task<bool> SetStateAsync(Guid agentId, AgentState from, AgentState to, CancellationToken ct = default)
    {
        if (!AgentLifecycle.CanMove(from, to))
            throw new InvalidOperationException($"An agent cannot move from {from} to {to}.");

        await using var cmd = dataSource.CreateCommand("""
            UPDATE agent SET state = @to, updated_at = now()
            WHERE agent_id = @agent_id AND state = @from
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("from", AgentSql.ToDb(from));
        cmd.Parameters.AddWithValue("to", AgentSql.ToDb(to));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
