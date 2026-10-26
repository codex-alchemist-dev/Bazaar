using Npgsql;

namespace Bazaar.Server.Agents.Data;

/// <summary>SQL-first access to <c>turn</c> (doc 06, "Turn loop").</summary>
public sealed class TurnRepository(NpgsqlDataSource dataSource)
{
    /// <summary>Starts a turn in <see cref="TurnStatus.Running"/>. Returns its id.</summary>
    public async Task<Guid> StartAsync(TurnStart turn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.WakeReason);
        ArgumentOutOfRangeException.ThrowIfNegative(turn.Depth);
        if ((turn.ParentTurnId is null) != (turn.Depth == 0))
            throw new ArgumentException("A turn has a parent if and only if its depth is above zero.", nameof(turn));

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO turn (agent_id, session_id, role, parent_turn_id, depth, wake_reason, context_manifest)
            VALUES (@agent_id, @session_id, @role, @parent_turn_id, @depth, @wake_reason, @manifest::jsonb)
            RETURNING turn_id
            """);
        cmd.Parameters.AddWithValue("agent_id", turn.AgentId);
        cmd.Parameters.AddWithValue("session_id", turn.SessionId);
        cmd.Parameters.AddWithValue("role", AgentSql.ToDb(turn.Role));
        cmd.Parameters.AddWithValue("parent_turn_id", AgentSql.Nullable(turn.ParentTurnId));
        cmd.Parameters.AddWithValue("depth", turn.Depth);
        cmd.Parameters.AddWithValue("wake_reason", turn.WakeReason);
        cmd.Parameters.Add(new NpgsqlParameter("manifest", turn.ManifestJson));
        return (Guid)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Ends a running turn with a final status. Returns false if the turn had already ended.</summary>
    public async Task<bool> EndAsync(Guid turnId, TurnStatus status, CancellationToken ct = default)
    {
        if (status == TurnStatus.Running)
            throw new ArgumentException("A turn ends in a final status, not Running.", nameof(status));

        await using var cmd = dataSource.CreateCommand("""
            UPDATE turn SET status = @status, ended_at = now()
            WHERE turn_id = @turn_id AND status = 'running'
            """);
        cmd.Parameters.AddWithValue("turn_id", turnId);
        cmd.Parameters.AddWithValue("status", AgentSql.ToDb(status));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<TurnRecord?> GetAsync(Guid turnId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"{_selectSql} WHERE turn_id = @turn_id");
        cmd.Parameters.AddWithValue("turn_id", turnId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Turns still marked running for an agent. Crash recovery reads these (doc 06).</summary>
    public async Task<IReadOnlyList<TurnRecord>> ListRunningAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"{_selectSql} WHERE agent_id = @agent_id AND status = 'running' ORDER BY started_at, turn_id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        var result = new List<TurnRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(Read(reader));
        return result;
    }

    private const string _selectSql = """
        SELECT turn_id, agent_id, session_id, role, parent_turn_id, depth, wake_reason, status, started_at, ended_at,
               context_manifest::text
        FROM turn
        """;

    private static TurnRecord Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetGuid(2),
        AgentSql.FromDb<TurnRole>(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetGuid(4),
        r.GetInt32(5),
        r.GetString(6),
        AgentSql.FromDb<TurnStatus>(r.GetString(7)),
        r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
        r.GetString(10));
}
