using Bazaar.Server.Gateway.Policy;
using Npgsql;

namespace Bazaar.Server.Gateway.Data;

/// <summary>
/// Per-agent model policy (<c>agent_model_policy</c>). Writes validate through <see cref="AgentModelPolicy.Create"/> first,
/// so a wildcard, <c>auto</c> route or unqualified entry is never stored. Reads fail closed: a stored policy that no
/// longer parses reads as no policy, which the resolver denies.
/// </summary>
public sealed class AgentModelPolicyRepository(NpgsqlDataSource dataSource, TimeProvider? time = null) : IAgentPolicyStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async ValueTask<AgentModelPolicy?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var row = await GetRowAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        try
        {
            return AgentModelPolicy.Create(row.AgentId, row.Allowed, row.DefaultRoute, row.Version);
        }
        catch (PolicyFormatException)
        {
            return null;
        }
    }

    public async Task<AgentModelPolicyRow?> GetRowAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT agent_id, allowed::text, default_route, version, changed_by, changed_at FROM agent_model_policy WHERE agent_id = $1");
        cmd.Parameters.AddWithValue(agentId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AgentModelPolicyRow(
            reader.GetString(0),
            GatewayJson.ReadStrings(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5));
    }

    /// <summary>
    /// Replaces an agent's policy and bumps its version (1 for a new agent). Only the owner's admin path calls this;
    /// no agent edits policy. Throws <see cref="PolicyFormatException"/> for an unacceptable entry.
    /// </summary>
    public async Task<long> SaveAsync(
        string agentId,
        IEnumerable<string> entries,
        string? defaultRoute,
        string changedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        var policy = AgentModelPolicy.Create(agentId, entries, defaultRoute, 1);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO agent_model_policy (agent_id, allowed, default_route, version, changed_by, changed_at)
            VALUES ($1, $2, $3, 1, $4, $5)
            ON CONFLICT (agent_id) DO UPDATE SET allowed = EXCLUDED.allowed, default_route = EXCLUDED.default_route,
                version = agent_model_policy.version + 1, changed_by = EXCLUDED.changed_by, changed_at = EXCLUDED.changed_at
            RETURNING version
            """);
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.Add(GatewayJson.Jsonb(GatewayJson.Array(policy.Entries)));
        cmd.Parameters.AddWithValue((object?)defaultRoute ?? DBNull.Value);
        cmd.Parameters.AddWithValue(changedBy);
        cmd.Parameters.AddWithValue(_time.GetUtcNow());
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
