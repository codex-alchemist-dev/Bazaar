using Bazaar.Server.Kanban.Data;
using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// SQL-first access to <c>ryusei_trigger</c> (doc 38, Data model). Writes take the caller's transaction so the trigger
/// service can lock, check and write in one unit. Nothing here checks authority or caps; that is the service's job.
/// </summary>
public sealed class RyuseiTriggerRepository(NpgsqlDataSource dataSource)
{
    private const string _columns = """
        trigger_id, project_id, created_by, kind, target_agent_ids, purpose, spec::text, task_id, attach_gc_id,
        ping_mode, state, next_fire_at, fire_count, consecutive_refusals, max_fires, expires_at, chain_depth,
        version, created_at, updated_at
        """;

    private const string _insertSql = """
        INSERT INTO ryusei_trigger (trigger_id, project_id, created_by, kind, target_agent_ids, purpose, spec, task_id,
                                    attach_gc_id, ping_mode, state, next_fire_at, max_fires, expires_at, chain_depth)
        VALUES (@trigger_id, @project_id, @created_by, @kind, @target_agent_ids, @purpose, @spec::jsonb, @task_id,
                @attach_gc_id, @ping_mode, 'active', @next_fire_at, @max_fires, @expires_at, @chain_depth)
        RETURNING
        """;

    // Writes the runtime columns and bumps the version only when the expected version matches.
    private const string _updateSql = """
        UPDATE ryusei_trigger
        SET state = @state, next_fire_at = @next_fire_at, fire_count = @fire_count,
            consecutive_refusals = @consecutive_refusals, version = version + 1, updated_at = now()
        WHERE trigger_id = @trigger_id AND version = @expected_version
        """;

    /// <summary>Reads one trigger without locking it.</summary>
    public async Task<RyuseiTriggerRecord?> GetAsync(Guid triggerId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM ryusei_trigger WHERE trigger_id = @trigger_id");
        cmd.Parameters.AddWithValue("trigger_id", triggerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Reads one trigger and holds a row lock on it until <paramref name="tx"/> ends.</summary>
    public async Task<RyuseiTriggerRecord?> GetForUpdateAsync(NpgsqlTransaction tx, Guid triggerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, $"SELECT {_columns} FROM ryusei_trigger WHERE trigger_id = @trigger_id FOR UPDATE");
        cmd.Parameters.AddWithValue("trigger_id", triggerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>
    /// Claims up to <paramref name="limit"/> active timer and interval triggers that are due at <paramref name="now"/>, locking
    /// them until <paramref name="tx"/> ends (doc 38, Firing). Rows another tick has locked are skipped, not waited for, so two
    /// ticks never fire the same trigger. A watcher is claimed only when an event has armed it (its next_fire_at is set). Events
    /// are never claimed: they fire on their first match in the event bridge.
    /// </summary>
    public async Task<IReadOnlyList<RyuseiTriggerRecord>> ClaimDueAsync(
        NpgsqlTransaction tx,
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = KanbanCommands.Create(dataSource, tx, $"""
            SELECT {_columns} FROM ryusei_trigger
            WHERE state = 'active' AND kind IN ('timer', 'interval', 'watcher') AND next_fire_at <= @now
            ORDER BY next_fire_at, trigger_id
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("limit", limit);
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Locks the active event and watcher triggers an event could match, in trigger id order (doc 38, WP-R6). A null
    /// <paramref name="projectId"/> means a GC-sourced event, which carries no project, so every project's triggers are
    /// candidates and the predicate's GC filter does the scoping. Rows are waited for, not skipped, so no match is missed.
    /// </summary>
    public async Task<IReadOnlyList<RyuseiTriggerRecord>> LockEventCandidatesAsync(
        NpgsqlTransaction tx,
        Guid? projectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, $"""
            SELECT {_columns} FROM ryusei_trigger
            WHERE state = 'active' AND kind IN ('event', 'watcher')
              AND (@project_id::uuid IS NULL OR project_id = @project_id)
            ORDER BY trigger_id
            FOR UPDATE
            """);
        cmd.Parameters.AddWithValue("project_id", KanbanCommands.Nullable(projectId));
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Lists a project's triggers, optionally in one state, oldest first.</summary>
    public async Task<IReadOnlyList<RyuseiTriggerRecord>> ListByProjectAsync(
        Guid projectId,
        RyuseiTriggerState? state = null,
        CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {_columns} FROM ryusei_trigger
            WHERE project_id = @project_id AND (@state::text IS NULL OR state = @state)
            ORDER BY created_at, trigger_id
            """);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("state", KanbanCommands.Nullable(state is null ? null : RyuseiText.ToSql(state.Value)));
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Lists every trigger that names an agent as a target, oldest first, whatever its state.</summary>
    public async Task<IReadOnlyList<RyuseiTriggerRecord>> ListByTargetAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {_columns} FROM ryusei_trigger
            WHERE @agent_id = ANY(target_agent_ids)
            ORDER BY created_at, trigger_id
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the active and paused triggers that name each of the agents. An agent with none is absent from the result.
    /// Runs in the caller's transaction so it sees the lock the service holds.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountLiveByTargetAsync(
        NpgsqlTransaction tx,
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(agentIds);
        await using var cmd = KanbanCommands.Create(dataSource, tx, """
            SELECT agent.agent_id, count(*)
            FROM ryusei_trigger AS r
            CROSS JOIN LATERAL unnest(r.target_agent_ids) AS agent(agent_id)
            WHERE agent.agent_id = ANY(@agent_ids) AND r.state IN ('active', 'paused')
            GROUP BY agent.agent_id
            """);
        cmd.Parameters.AddWithValue("agent_ids", agentIds.ToArray());
        var counts = new Dictionary<Guid, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            counts[reader.GetGuid(0)] = checked((int)reader.GetInt64(1));
        }

        return counts;
    }

    /// <summary>Inserts a trigger in state active and returns the stored row.</summary>
    public async Task<RyuseiTriggerRecord> InsertAsync(NpgsqlTransaction tx, NewRyuseiTrigger trigger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(trigger);
        await using var cmd = KanbanCommands.Create(dataSource, tx, $"{_insertSql} {_columns}");
        cmd.Parameters.AddWithValue("trigger_id", trigger.TriggerId);
        cmd.Parameters.AddWithValue("project_id", trigger.ProjectId);
        cmd.Parameters.AddWithValue("created_by", trigger.CreatedBy);
        cmd.Parameters.AddWithValue("kind", RyuseiText.ToSql(trigger.Kind));
        cmd.Parameters.AddWithValue("target_agent_ids", trigger.TargetAgentIds.ToArray());
        cmd.Parameters.AddWithValue("purpose", trigger.Purpose);
        cmd.Parameters.AddWithValue("spec", trigger.SpecJson);
        cmd.Parameters.AddWithValue("task_id", KanbanCommands.Nullable(trigger.TaskId));
        cmd.Parameters.AddWithValue("attach_gc_id", KanbanCommands.Nullable(trigger.AttachGcId));
        cmd.Parameters.AddWithValue("ping_mode", RyuseiText.ToSql(trigger.PingMode));
        cmd.Parameters.AddWithValue("next_fire_at", KanbanCommands.Nullable(trigger.NextFireAt));
        cmd.Parameters.AddWithValue("max_fires", KanbanCommands.Nullable(trigger.MaxFires));
        cmd.Parameters.AddWithValue("expires_at", KanbanCommands.Nullable(trigger.ExpiresAt));
        cmd.Parameters.AddWithValue("chain_depth", trigger.ChainDepth);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return Read(reader);
    }

    /// <summary>
    /// Writes the runtime columns of <paramref name="next"/> (state, next fire, fire and refusal counts) if the stored
    /// version is still <paramref name="expectedVersion"/>. Returns false, and changes nothing, when another writer got
    /// there first. On success the version is expectedVersion + 1.
    /// </summary>
    public async Task<bool> UpdateAsync(NpgsqlTransaction tx, RyuseiTriggerRecord next, long expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(next);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _updateSql);
        cmd.Parameters.AddWithValue("trigger_id", next.TriggerId);
        cmd.Parameters.AddWithValue("expected_version", expectedVersion);
        cmd.Parameters.AddWithValue("state", RyuseiText.ToSql(next.State));
        cmd.Parameters.AddWithValue("next_fire_at", KanbanCommands.Nullable(next.NextFireAt));
        cmd.Parameters.AddWithValue("fire_count", next.FireCount);
        cmd.Parameters.AddWithValue("consecutive_refusals", next.ConsecutiveRefusals);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<RyuseiTriggerRecord>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var result = new List<RyuseiTriggerRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    private static RyuseiTriggerRecord Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetGuid(2),
        RyuseiText.ParseKind(r.GetString(3)),
        r.GetFieldValue<Guid[]>(4),
        r.GetString(5),
        r.GetString(6),
        r.IsDBNull(7) ? null : r.GetGuid(7),
        r.IsDBNull(8) ? null : r.GetGuid(8),
        RyuseiText.ParsePingMode(r.GetString(9)),
        RyuseiText.ParseState(r.GetString(10)),
        r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
        r.GetInt64(12),
        r.GetInt32(13),
        r.IsDBNull(14) ? null : r.GetInt64(14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        r.GetInt32(16),
        r.GetInt64(17),
        r.GetFieldValue<DateTimeOffset>(18),
        r.GetFieldValue<DateTimeOffset>(19));
}
