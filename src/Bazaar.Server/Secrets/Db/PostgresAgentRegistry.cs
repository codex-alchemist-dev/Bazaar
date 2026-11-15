using System.Text.Json.Nodes;
using Bazaar.Secrets;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Secrets.Db;

/// <summary>
/// Agent identities and lifecycle state in <c>secret_agent</c> (migration secrets/0001). Each real transition locks the row,
/// checks <see cref="AgentTransitionRules"/>, updates the state and appends its audit entry in one transaction, so the change and
/// its record commit together or not at all.
/// </summary>
public sealed class PostgresAgentRegistry(NpgsqlDataSource dataSource, ITransactionalAuditLog audit, TimeProvider time) : IAgentRegistry
{
    private const string _auditKind = "secrets.agent.transition";

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

    public async Task<AgentRecord> TransitionAsync(AgentId id, AgentState target, OwnerRelease? release, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        AgentState from;
        await using (var select = new NpgsqlCommand("SELECT state FROM secret_agent WHERE agent_id = $1 FOR UPDATE", conn, tx))
        {
            select.Parameters.AddWithValue(id.Value);
            var value = await select.ExecuteScalarAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Agent '{id.Value}' is not registered.");
            from = Enum.Parse<AgentState>((string)value, ignoreCase: false);
        }

        if (from == target)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return await GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Agent '{id.Value}' disappeared while checking its state.");
        }

        var denial = AgentTransitionRules.Denial(from, target, release);
        if (denial is not null)
        {
            throw new AgentTransitionException(denial);
        }

        var now = time.GetUtcNow();
        await using (var update = new NpgsqlCommand(
            "UPDATE secret_agent SET state = $2, updated_at = $3 WHERE agent_id = $1 AND state = $4", conn, tx))
        {
            update.Parameters.AddWithValue(id.Value);
            update.Parameters.AddWithValue(target.ToString());
            update.Parameters.AddWithValue(now);
            update.Parameters.AddWithValue(from.ToString());
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Agent '{id.Value}' changed state while this transition ran; retry it.");
            }
        }

        var payload = new JsonObject
        {
            ["agent"] = id.Value,
            ["from"] = from.ToString(),
            ["to"] = target.ToString(),
            ["reason"] = release?.Reason,
        };
        await audit.AppendInTransactionAsync(
            conn, tx, new AuditEntry(now, release?.Owner ?? "host:secret-store", _auditKind, payload.ToJsonString()), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new AgentRecord(id, target, now);
    }
}
