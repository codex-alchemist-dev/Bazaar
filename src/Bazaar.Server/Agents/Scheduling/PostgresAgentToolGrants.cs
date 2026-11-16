using Npgsql;

namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// Reads and writes <c>agent_tool_grant</c> (migration <c>agent_turns/0002</c>). Writes are for the grant path that a Tier 2
/// approval drives (doc 07, "Promotion"); this class does not decide whether a change is approved.
/// </summary>
public sealed class PostgresAgentToolGrants(NpgsqlDataSource dataSource) : IAgentToolGrants
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GrantedToolsAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT tool FROM agent_tool_grant WHERE agent_id = @agent AND revoked_at IS NULL ORDER BY tool");
        command.Parameters.AddWithValue("agent", agentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var tools = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tools.Add(reader.GetString(0));
        }

        return tools;
    }

    /// <summary>Grants a tool to an agent, or reactivates a revoked grant.</summary>
    public async Task GrantAsync(Guid agentId, string tool, string grantedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(grantedBy);
        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO agent_tool_grant (agent_id, tool, granted_by) VALUES (@agent, @tool, @by)
            ON CONFLICT (agent_id, tool) DO UPDATE SET revoked_at = NULL, granted_by = EXCLUDED.granted_by, granted_at = now()
            """);
        command.Parameters.AddWithValue("agent", agentId);
        command.Parameters.AddWithValue("tool", tool);
        command.Parameters.AddWithValue("by", grantedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes a grant. The row stays, so the history of the grant is kept.</summary>
    public async Task RevokeAsync(Guid agentId, string tool, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "UPDATE agent_tool_grant SET revoked_at = now() WHERE agent_id = @agent AND tool = @tool AND revoked_at IS NULL");
        command.Parameters.AddWithValue("agent", agentId);
        command.Parameters.AddWithValue("tool", tool);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
