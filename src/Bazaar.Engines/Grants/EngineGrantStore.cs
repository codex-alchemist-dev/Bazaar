using Bazaar.Contracts.Engines;
using Npgsql;

namespace Bazaar.Engines.Grants;

/// <summary>
/// Issues and checks rows in <c>engine_grants</c> (migration <c>engines/0001</c>). SQL-first over one shared
/// <see cref="NpgsqlDataSource"/>; no ORM.
/// </summary>
/// <remarks>
/// TODO(component): each grant is also an approval row in doc 15's <c>approval</c> table (one approval table, C16). This store
/// does not write that row yet, and <c>granted_by</c> stays free text until doc 15's schema is settled.
/// </remarks>
public sealed class EngineGrantStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Inserts a grant, or replaces the risk class, granter and time of the existing row for the same
    /// (agent, adapter, tool). Re-issuing is an explicit approval act, so the new values win.
    /// </summary>
    public async Task IssueAsync(EngineGrant grant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.AdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.ToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.GrantedBy);

        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO engine_grants (agent_id, adapter_id, tool_name, risk_class, granted_by, granted_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6) " +
            "ON CONFLICT (agent_id, adapter_id, tool_name) DO UPDATE SET " +
            "risk_class = EXCLUDED.risk_class, granted_by = EXCLUDED.granted_by, granted_at = EXCLUDED.granted_at");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = grant.AgentId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = grant.AdapterId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = grant.ToolName });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ToSql(grant.RiskClass) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = grant.GrantedBy });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = grant.GrantedAt });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the call may run. <c>read</c> calls always pass. <c>write</c> and <c>exec</c> calls pass only when a row exists
    /// for the same agent, adapter and tool with the same risk class; otherwise they are denied (default deny).
    /// </summary>
    public async Task<bool> CheckAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (!EngineGrantPolicy.RequiresGrant(riskClass))
        {
            return true;
        }

        await using var cmd = dataSource.CreateCommand(
            "SELECT risk_class FROM engine_grants WHERE agent_id = $1 AND adapter_id = $2 AND tool_name = $3");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = adapterId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = toolName });
        var stored = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return stored is not null && stored == ToSql(riskClass);
    }

    /// <summary>Stored spelling of a risk class; matches the CHECK constraint in <c>engines/0001</c>.</summary>
    private static string ToSql(RiskClass riskClass) => riskClass switch
    {
        RiskClass.Read => "read",
        RiskClass.Write => "write",
        RiskClass.Exec => "exec",
        _ => throw new ArgumentOutOfRangeException(nameof(riskClass), riskClass, "Unknown risk class."),
    };
}
