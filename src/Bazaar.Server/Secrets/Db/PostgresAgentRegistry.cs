using Bazaar.Secrets;
using Npgsql;

namespace Bazaar.Server.Secrets.Db;

/// <summary>Agent identities and lifecycle state in <c>secret_agent</c> (migration secrets/0001).</summary>
public sealed class PostgresAgentRegistry(NpgsqlDataSource dataSource, TimeProvider time) : IAgentRegistry
{
    /// <summary>Inserts the agent as Active when it is new, then returns the stored row, so an existing agent keeps its state.</summary>
    public async Task<AgentRecord> RegisterAsync(AgentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using (var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO secret_agent (agent_id, state, updated_at) VALUES ($1, 'Active', $2) ON CONFLICT (agent_id) DO NOTHING", conn))
        {
            insert.Parameters.AddWithValue(id.Value);
            insert.Parameters.AddWithValue(time.GetUtcNow());
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return await GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent '{id.Value}' is missing right after registration.");
    }

    public async Task<AgentRecord?> GetAsync(AgentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT agent_id, state, updated_at FROM secret_agent WHERE agent_id = $1", conn);
        cmd.Parameters.AddWithValue(id.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new AgentRecord(
            new AgentId(reader.GetString(0)),
            Enum.Parse<AgentState>(reader.GetString(1), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task<AgentRecord> SetStateAsync(AgentId id, AgentState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "UPDATE secret_agent SET state = $2, updated_at = $3 WHERE agent_id = $1", conn);
        cmd.Parameters.AddWithValue(id.Value);
        cmd.Parameters.AddWithValue(state.ToString());
        cmd.Parameters.AddWithValue(time.GetUtcNow());
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Agent '{id.Value}' is not registered.");
        }

        return await GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent '{id.Value}' disappeared after its state changed.");
    }
}
