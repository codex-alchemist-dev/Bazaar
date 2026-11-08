using Npgsql;

namespace Bazaar.Server.Interrupts;

/// <summary>
/// SQL-first interrupt stack over <c>interrupt_frame</c> (doc 24 section 3). Every push and pop runs in one transaction under
/// a per-agent advisory lock, so two writers cannot both see room for a frame. The stack is read from the table, never from
/// memory, so a restarted host sees the same top frame.
/// </summary>
public sealed class InterruptStackService(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null) : IInterruptStackService
{
    /// <summary>Identical system pushes inside this window collapse into the live frame.</summary>
    public static readonly TimeSpan SystemDedupeWindow = TimeSpan.FromSeconds(60);

    /// <summary>Most non-terminal frames an agent may hold at once (one active, one suspended).</summary>
    public const int MaxLiveFrames = 2;

    private const string _columns = "id, agent_id, parent_frame_id, source, reason, gc_id, state, pushed_at, ttl_at, version";

    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@key::text, 0))";

    private const string _liveSql = $"""
        SELECT {_columns} FROM interrupt_frame
        WHERE agent_id = @agent_id AND state IN ('active', 'suspended')
        ORDER BY (state = 'active') DESC, pushed_at DESC, id DESC
        """;

    private const string _findSql = $"SELECT {_columns} FROM interrupt_frame WHERE id = @id AND agent_id = @agent_id";

    private const string _suspendSql = """
        UPDATE interrupt_frame SET state = 'suspended', version = version + 1
        WHERE id = @id AND state = 'active'
        """;

    private const string _insertSql = """
        INSERT INTO interrupt_frame (id, agent_id, parent_frame_id, source, reason, gc_id, state, pushed_at, ttl_at, version)
        VALUES (@id, @agent_id, @parent_frame_id, @source, @reason, @gc_id, 'active', @pushed_at, @ttl_at, 1)
        """;

    private const string _doneSql = """
        UPDATE interrupt_frame SET state = 'done', version = version + 1
        WHERE id = @id AND state = 'active'
        """;

    private const string _resumeSql = """
        UPDATE interrupt_frame SET state = 'active', version = version + 1
        WHERE id = @id AND state = 'suspended'
        """;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<InterruptFrame> PushAsync(
        Guid agentId, InterruptSource source, string reason, Guid gcId, DateTimeOffset? ttlAt = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await LockAgentAsync(conn, tx, agentId, ct).ConfigureAwait(false);
        var live = await ReadLiveAsync(conn, tx, agentId, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        if (IsSystem(source))
        {
            var twin = live.FirstOrDefault(f =>
                f.Source == source && f.Reason == reason && f.GcId == gcId && now - f.PushedAt <= SystemDedupeWindow);
            if (twin is not null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return twin;
            }
        }

        if (live.Count >= MaxLiveFrames)
            throw new InterruptDepthExceededException(agentId);

        var top = live.FirstOrDefault(f => f.State == InterruptFrameState.Active);
        if (top is not null)
            await ExecuteAsync(conn, tx, _suspendSql, ("id", top.Id), ct).ConfigureAwait(false);

        var frame = new InterruptFrame(
            Guid.CreateVersion7(), agentId, top?.Id, source, reason, gcId, InterruptFrameState.Active, now, ttlAt, 1);
        await using (var insert = Command(conn, tx, _insertSql))
        {
            insert.Parameters.AddWithValue("id", frame.Id);
            insert.Parameters.AddWithValue("agent_id", agentId);
            insert.Parameters.AddWithValue("parent_frame_id", (object?)frame.ParentFrameId ?? DBNull.Value);
            insert.Parameters.AddWithValue("source", ToDb(source));
            insert.Parameters.AddWithValue("reason", reason);
            insert.Parameters.AddWithValue("gc_id", gcId);
            insert.Parameters.AddWithValue("pushed_at", now);
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset?>("ttl_at", ttlAt));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return frame;
    }

    /// <inheritdoc />
    public async Task<InterruptFrame?> PopAsync(Guid agentId, Guid frameId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await LockAgentAsync(conn, tx, agentId, ct).ConfigureAwait(false);
        var frame = await FindAsync(conn, tx, agentId, frameId, ct).ConfigureAwait(false)
            ?? throw new InterruptStackException($"Interrupt frame '{frameId}' does not belong to agent '{agentId}'.");

        if (frame.State is InterruptFrameState.Done or InterruptFrameState.Cancelled)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        if (frame.State != InterruptFrameState.Active)
            throw new InterruptFrameNotTopException(frameId);

        await ExecuteAsync(conn, tx, _doneSql, ("id", frameId), ct).ConfigureAwait(false);
        InterruptFrame? resumed = null;
        if (frame.ParentFrameId is { } parentId)
        {
            await ExecuteAsync(conn, tx, _resumeSql, ("id", parentId), ct).ConfigureAwait(false);
            resumed = await FindAsync(conn, tx, agentId, parentId, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return resumed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InterruptFrame>> GetStackAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadLiveAsync(conn, null, agentId, ct).ConfigureAwait(false);
    }

    private static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        if (tx is not null)
            cmd.Transaction = tx;
        return cmd;
    }

    private static Task LockAgentAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, CancellationToken ct) =>
        ExecuteAsync(conn, tx, _lockSql, ("key", $"interrupt_stack:{agentId:D}"), ct);

    private static async Task ExecuteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, (string Name, object Value) parameter, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, sql);
        cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<InterruptFrame>> ReadLiveAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid agentId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, _liveSql);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return await ReadFramesAsync(cmd, ct).ConfigureAwait(false);
    }

    private static async Task<InterruptFrame?> FindAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, Guid frameId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, _findSql);
        cmd.Parameters.AddWithValue("id", frameId);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return (await ReadFramesAsync(cmd, ct).ConfigureAwait(false)).SingleOrDefault();
    }

    private static async Task<IReadOnlyList<InterruptFrame>> ReadFramesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var frames = new List<InterruptFrame>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            frames.Add(new InterruptFrame(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                FromDbSource(reader.GetString(3)),
                reader.GetString(4),
                reader.GetGuid(5),
                FromDbState(reader.GetString(6)),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetInt64(9)));
        }

        return frames;
    }

    private static bool IsSystem(InterruptSource source) => source is not InterruptSource.Owner;

    private static string ToDb(InterruptSource source) => source switch
    {
        InterruptSource.Owner => "owner",
        InterruptSource.SystemBuild => "system_build",
        InterruptSource.SystemSecurity => "system_security",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown interrupt source."),
    };

    private static InterruptSource FromDbSource(string value) => value switch
    {
        "owner" => InterruptSource.Owner,
        "system_build" => InterruptSource.SystemBuild,
        "system_security" => InterruptSource.SystemSecurity,
        _ => throw new InvalidOperationException($"Unknown interrupt source '{value}' in the database."),
    };

    private static InterruptFrameState FromDbState(string value) => value switch
    {
        "active" => InterruptFrameState.Active,
        "suspended" => InterruptFrameState.Suspended,
        "done" => InterruptFrameState.Done,
        "cancelled" => InterruptFrameState.Cancelled,
        _ => throw new InvalidOperationException($"Unknown interrupt frame state '{value}' in the database."),
    };
}
