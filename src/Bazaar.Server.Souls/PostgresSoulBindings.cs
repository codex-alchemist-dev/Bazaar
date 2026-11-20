using Bazaar.Souls.Abstractions;
using Npgsql;

namespace Bazaar.Server.Souls;

/// <summary>
/// The agent-to-Soul mapping over <c>soul_agent_binding</c> (migration <c>souls/0002</c>) and <c>soul_versions</c>
/// (docs/design/07 "Versioning"). Reads and writes go through this class only; GC and the agent runtime see
/// <see cref="ISoulBindings"/>. Every change is a new row, and a per-agent advisory lock serialises writers so two binds cannot
/// interleave their read of the current state with their insert.
/// </summary>
public sealed class PostgresSoulBindings(NpgsqlDataSource dataSource) : ISoulBindings, ISoulBindingAdmin
{
    // The latest event for the agent, then the head of the bound Soul's chain. A retired agent has soul_id NULL in no row:
    // the retired event is the latest row and carries no Soul, so the join finds nothing.
    private const string _resolveSql = """
        SELECT b.soul_id, (SELECT max(v.version) FROM soul_versions v WHERE v.soul_id = b.soul_id)
        FROM (SELECT soul_id, event FROM soul_agent_binding WHERE agent_id = $1 ORDER BY seq DESC LIMIT 1) b
        WHERE b.event = 'bound'
        """;

    private const string _validStampSql = """
        SELECT EXISTS (SELECT 1 FROM soul_versions WHERE soul_id = $2 AND version = $3)
           AND EXISTS (SELECT 1 FROM soul_agent_binding WHERE agent_id = $1 AND soul_id = $2 AND event = 'bound')
        """;

    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";

    private const string _latestSql = "SELECT soul_id, event FROM soul_agent_binding WHERE agent_id = $1 ORDER BY seq DESC LIMIT 1";

    private const string _soulHasVersionSql = "SELECT EXISTS (SELECT 1 FROM soul_versions WHERE soul_id = $1)";

    private const string _insertSql = "INSERT INTO soul_agent_binding (agent_id, soul_id, event, reason) VALUES ($1, $2, $3, $4)";

    /// <inheritdoc />
    public async ValueTask<SoulRef?> ResolveAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(_resolveSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(1))
            return null;
        return new SoulRef(reader.GetGuid(0), reader.GetInt32(1));
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsValidStampAsync(Guid agentId, SoulRef soul, CancellationToken cancellationToken)
    {
        if (!soul.IsValid)
            return false;
        await using var cmd = dataSource.CreateCommand(_validStampSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = soul.SoulId });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = soul.Version });
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task BindAsync(Guid agentId, Guid soulId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (agentId == Guid.Empty || soulId == Guid.Empty)
            throw new ArgumentException("The agent and Soul ids are required.");

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(conn, tx, agentId, cancellationToken).ConfigureAwait(false);

        var (currentSoul, currentEvent) = await LatestAsync(conn, tx, agentId, cancellationToken).ConfigureAwait(false);
        if (currentEvent == "retired")
            throw new InvalidOperationException("A retired agent cannot be bound to a Soul (docs/design/06, Retired).");
        if (currentEvent == "bound" && currentSoul == soulId)
            return;

        await using (var check = new NpgsqlCommand(_soulHasVersionSql, conn, tx))
        {
            check.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = soulId });
            if (!(bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                throw new InvalidOperationException("The Soul has no versions; append version 1 before binding an agent to it.");
        }

        await InsertAsync(conn, tx, agentId, soulId, "bound", reason.Trim(), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RetireAsync(Guid agentId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(conn, tx, agentId, cancellationToken).ConfigureAwait(false);

        var (_, currentEvent) = await LatestAsync(conn, tx, agentId, cancellationToken).ConfigureAwait(false);
        if (currentEvent != "bound")
            return;

        await InsertAsync(conn, tx, agentId, null, "retired", reason.Trim(), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_lockSql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = $"soul_agent_binding:{agentId:D}" });
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(Guid? SoulId, string? Event)> LatestAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_latestSql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1));
    }

    private static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid agentId, Guid? soulId, string kind, string reason, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_insertSql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        cmd.Parameters.Add(soulId is { } id ? new NpgsqlParameter<Guid> { TypedValue = id } : new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = reason });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
