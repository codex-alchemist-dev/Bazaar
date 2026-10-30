using Npgsql;

namespace Bazaar.Server.Skimmer.Data;

/// <summary>SQL-first access to <c>gate_config</c> (doc 13). Mode and thresholds are read from the settings registry, not here.</summary>
public sealed class GateConfigRepository(NpgsqlDataSource dataSource)
{
    private const string _upsertSql = """
        INSERT INTO gate_config (agent_id, subscriptions) VALUES (@agent_id, @subscriptions::jsonb)
        ON CONFLICT (agent_id) DO UPDATE SET subscriptions = EXCLUDED.subscriptions
        """;

    /// <summary>The gate config of one agent, or null when none was written.</summary>
    public async Task<GateConfig?> GetAsync(Guid agentId, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = SkimmerCommands.Create(dataSource, tx,
            "SELECT agent_id, subscriptions::text FROM gate_config WHERE agent_id = @agent_id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new GateConfig(reader.GetGuid(0), reader.GetString(1));
    }

    /// <summary>Writes the subscriptions for an agent, inserting the row on first write.</summary>
    public async Task UpsertAsync(GateConfig config, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await using var cmd = SkimmerCommands.Create(dataSource, tx, _upsertSql);
        cmd.Parameters.AddWithValue("agent_id", config.AgentId);
        cmd.Parameters.AddWithValue("subscriptions", config.SubscriptionsJson);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
