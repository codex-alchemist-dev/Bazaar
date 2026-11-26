using Npgsql;

namespace Bazaar.Server.Security.KillSwitch.Postgres;

/// <summary>
/// Postgres <see cref="IKillSwitchStateStore"/> over the <c>kill_switch_global</c> and <c>kill_switch_agent_stop</c> tables
/// (agent_safety migration 0003). Every instant is the caller's instant, never the database clock, so the engine's
/// <see cref="TimeProvider"/> stays the only source of time. Works under the runtime role (<c>bazaar_app</c>): SELECT, INSERT
/// and DELETE only.
/// </summary>
/// <remarks>
/// A replace runs delete then insert in one transaction, so a reader never sees a scope with no row, and a failed insert
/// rolls the delete back. Instants are cut to microseconds, the precision Postgres keeps, so a write and its read-back agree.
/// </remarks>
public sealed class PostgresKillSwitchStateStore(NpgsqlDataSource dataSource) : IKillSwitchStateStore
{
    private const string _loadGlobal = "SELECT actor_id, reason, engaged_at FROM kill_switch_global";
    private const string _loadAgents = "SELECT agent_id, reason, actor_id, engaged_at FROM kill_switch_agent_stop ORDER BY agent_id";
    private const string _deleteGlobal = "DELETE FROM kill_switch_global";
    private const string _insertGlobal = "INSERT INTO kill_switch_global (singleton, actor_id, reason, engaged_at) VALUES (true, $1, $2, $3)";
    private const string _listAgentIds = "SELECT agent_id FROM kill_switch_agent_stop";
    private const string _deleteAgent = "DELETE FROM kill_switch_agent_stop WHERE agent_id = $1";
    private const string _insertAgent =
        "INSERT INTO kill_switch_agent_stop (agent_id, scope, reason, actor_id, engaged_at) VALUES ($1, 'agent', $2, $3, $4)";

    /// <inheritdoc />
    public async Task<KillSwitchStatus> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        KillSwitchStop? global = null;
        await using (var globalCmd = new NpgsqlCommand(_loadGlobal, conn))
        await using (var reader = await globalCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                global = new KillSwitchStop(
                    KillSwitchScope.Global,
                    null,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2));
            }
        }

        var byAgent = new SortedDictionary<string, KillSwitchStop>(StringComparer.Ordinal);
        await using (var agentCmd = new NpgsqlCommand(_loadAgents, conn))
        await using (var reader = await agentCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // A row written before ids were canonicalised may carry another spelling; two spellings of one agent keep the earlier stop.
                var key = AgentIds.Canonical(reader.GetString(0));
                var stop = new KillSwitchStop(
                    KillSwitchScope.Agent,
                    key,
                    reader.GetString(2),
                    reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(3));
                if (!byAgent.TryGetValue(key, out var kept) || stop.Since < kept.Since)
                {
                    byAgent[key] = stop;
                }
            }
        }

        return new KillSwitchStatus(global, byAgent.Values.ToArray());
    }

    /// <inheritdoc />
    public async Task SetGlobalAsync(KillSwitchStop engaged, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engaged);
        ct.ThrowIfCancellationRequested();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var deleteCmd = new NpgsqlCommand(_deleteGlobal, conn, tx))
        {
            await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var insertCmd = new NpgsqlCommand(_insertGlobal, conn, tx))
        {
            insertCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = engaged.ActorId });
            insertCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = engaged.Reason });
            insertCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(engaged.Since) });
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearGlobalAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_deleteGlobal);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><see cref="KillSwitchStop.AgentId"/> is null, empty or whitespace.</exception>
    public async Task SetAgentStopAsync(KillSwitchStop engaged, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engaged);
        ArgumentException.ThrowIfNullOrWhiteSpace(engaged.AgentId);
        ct.ThrowIfCancellationRequested();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var canonical = AgentIds.Canonical(engaged.AgentId);
        await DeleteAgentSpellingsAsync(conn, tx, canonical, ct).ConfigureAwait(false);
        await using (var insertCmd = new NpgsqlCommand(_insertAgent, conn, tx))
        {
            insertCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = canonical });
            insertCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = engaged.Reason });
            insertCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = engaged.ActorId });
            insertCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(engaged.Since) });
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    public async Task ClearAgentStopAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await DeleteAgentSpellingsAsync(conn, tx, AgentIds.Canonical(agentId), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every stored row of one agent, whatever its spelling. The ids are compared in code (not in SQL), so rows written
    /// before ids were canonicalised match too and no migration is needed. The table holds one row per stopped agent.
    /// </summary>
    private static async Task DeleteAgentSpellingsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string canonical, CancellationToken ct)
    {
        var spellings = new List<string>();
        await using (var listCmd = new NpgsqlCommand(_listAgentIds, conn, tx))
        await using (var reader = await listCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var stored = reader.GetString(0);
                if (string.Equals(AgentIds.Canonical(stored), canonical, StringComparison.Ordinal))
                {
                    spellings.Add(stored);
                }
            }
        }

        foreach (var stored in spellings)
        {
            await using var deleteCmd = new NpgsqlCommand(_deleteAgent, conn, tx);
            deleteCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = stored });
            await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
