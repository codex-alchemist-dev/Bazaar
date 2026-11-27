using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Server.AgentSafety.AutoContainment;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Persistence;

/// <summary>
/// Postgres <see cref="IContainmentRecordStore"/> over the <c>auto_containment</c> table (agent_safety migration 0005). The partial
/// unique index allows one InForce record per agent, so concurrent adds cannot both win. Runs under the runtime role
/// (<c>bazaar_app</c>): SELECT, INSERT and UPDATE of <c>state</c> only.
/// </summary>
public sealed class PostgresContainmentRecordStore(NpgsqlDataSource dataSource) : IContainmentRecordStore
{
    private const string _add =
        "INSERT INTO auto_containment (id, agent_id, rule_id, severity, contained_at, undo_until, state) VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING";
    private const string _latest =
        "SELECT id, agent_id, rule_id, severity, contained_at, undo_until, state FROM auto_containment WHERE agent_id = $1 ORDER BY contained_at DESC, id LIMIT 1";
    private const string _transition = "UPDATE auto_containment SET state = $3 WHERE id = $1 AND state = $2";
    private const string _expire = "UPDATE auto_containment SET state = 'WindowExpired' WHERE state = 'InForce' AND undo_until <= $1";

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(ContainmentRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.AgentId);
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_add);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = record.Id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.AgentId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.RuleId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.Severity.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(record.ContainedAt) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(record.UndoUntil) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = record.State.ToString() });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<ContainmentRecord?> GetLatestAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_latest);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ContainmentRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<AnomalySeverity>(reader.GetString(3)),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            Enum.Parse<ContainmentRecordState>(reader.GetString(6)));
    }

    /// <inheritdoc />
    public async Task<bool> TryTransitionAsync(Guid id, ContainmentRecordState expected, ContainmentRecordState target, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_transition);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = expected.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = target.ToString() });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_expire);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(now) });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
