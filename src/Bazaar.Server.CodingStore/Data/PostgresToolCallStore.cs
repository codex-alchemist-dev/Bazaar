using Bazaar.Contracts.Coding;
using Npgsql;

namespace Bazaar.Server.CodingStore.Data;

/// <summary>
/// SQL-first store over the append-only <c>tool_calls</c> table (migration coding/0001, doc 17 WP2). Inserts rows, records an
/// outcome once, and reads rows back. The table's trigger, not this class, enforces append-only; no method here updates
/// anything except the outcome columns.
/// </summary>
public sealed class PostgresToolCallStore(NpgsqlDataSource dataSource) : IToolCallLog, IToolCallReader
{
    private const string _columns =
        "id, session_id, tool, capability_class, source_trust, tainted_at_write, status, exit_code, args_sha256, started_at, ended_at";

    private const string _insertSql = """
        INSERT INTO tool_calls (id, session_id, tool, capability_class, source_trust, tainted_at_write, status, exit_code,
            args_sha256, started_at, ended_at)
        VALUES (@id, @session_id, @tool, @capability_class, @source_trust, @tainted_at_write, @status, @exit_code,
            @args_sha256, @started_at, @ended_at)
        """;

    // Matches only a row still at 'unknown', so a repeat or a second outcome changes nothing and returns false.
    private const string _completeSql = """
        UPDATE tool_calls SET status = @status, exit_code = @exit_code, ended_at = @ended_at
        WHERE id = @id AND status = 'unknown'
        """;

    private readonly TimeProvider _time = TimeProvider.System;

    /// <inheritdoc />
    public async ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var cmd = dataSource.CreateCommand(_insertSql);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("session_id", record.SessionId);
        cmd.Parameters.AddWithValue("tool", record.Tool);
        cmd.Parameters.AddWithValue("capability_class", record.CapabilityClass);
        cmd.Parameters.AddWithValue("source_trust", record.SourceTrust);
        cmd.Parameters.AddWithValue("tainted_at_write", record.TaintedAtWrite);
        cmd.Parameters.AddWithValue("status", ToWire(record.Status));
        cmd.Parameters.AddWithValue("exit_code", (object?)record.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("args_sha256", record.ArgsSha256);
        cmd.Parameters.AddWithValue("started_at", record.StartedAt);
        cmd.Parameters.AddWithValue("ended_at", (object?)record.EndedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> CompleteAsync(Guid id, ToolCallStatus status, int? exitCode, CancellationToken cancellationToken)
    {
        if (status == ToolCallStatus.Unknown)
            throw new ArgumentException("Unknown is not an outcome.", nameof(status));
        await using var cmd = dataSource.CreateCommand(_completeSql);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", ToWire(status));
        cmd.Parameters.AddWithValue("exit_code", (object?)exitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ended_at", _time.GetUtcNow());
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async ValueTask<ToolCallRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM tool_calls WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolCallRecord>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM tool_calls WHERE session_id = @session_id ORDER BY started_at, id");
        cmd.Parameters.AddWithValue("session_id", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ToolCallRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(Map(reader));
        return rows;
    }

    private static ToolCallRecord Map(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetBoolean(5),
        FromWire(r.GetString(6)),
        r.IsDBNull(7) ? null : r.GetInt32(7),
        r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10));

    private static string ToWire(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Succeeded => "succeeded",
        ToolCallStatus.Failed => "failed",
        ToolCallStatus.TimedOut => "timed_out",
        ToolCallStatus.Unknown => "unknown",
        ToolCallStatus.Refused => "refused",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static ToolCallStatus FromWire(string value) => value switch
    {
        "succeeded" => ToolCallStatus.Succeeded,
        "failed" => ToolCallStatus.Failed,
        "timed_out" => ToolCallStatus.TimedOut,
        "unknown" => ToolCallStatus.Unknown,
        "refused" => ToolCallStatus.Refused,
        _ => throw new InvalidOperationException($"Unknown tool_calls status '{value}'."),
    };
}
