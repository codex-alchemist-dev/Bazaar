using Npgsql;

namespace Bazaar.Server.Agents.Data;

/// <summary>
/// SQL-first access to <c>turn_step</c>. A step is written as pending before the action it describes, then completed
/// in place. Recovery marks an unfinished step abandoned (safe to re-issue) or unknown (the model is told).
/// </summary>
public sealed class TurnStepRepository(NpgsqlDataSource dataSource)
{
    /// <summary>Writes a pending step with the next step number for the turn. Returns that number.</summary>
    public async Task<int> BeginAsync(
        Guid turnId, TurnStepKind kind, string requestHash, bool idempotent = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO turn_step (turn_id, n, kind, status, request_hash, idempotent)
            VALUES (@turn_id,
                    (SELECT COALESCE(MAX(n), 0) + 1 FROM turn_step WHERE turn_id = @turn_id),
                    @kind, 'pending', @request_hash, @idempotent)
            RETURNING n
            """);
        cmd.Parameters.AddWithValue("turn_id", turnId);
        cmd.Parameters.AddWithValue("kind", AgentSql.ToDb(kind));
        cmd.Parameters.AddWithValue("request_hash", requestHash);
        cmd.Parameters.AddWithValue("idempotent", idempotent);
        return (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Completes a pending step with its response. Returns false if the step is not pending.</summary>
    public async Task<bool> CompleteAsync(Guid turnId, int n, string responseJson, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE turn_step SET status = 'completed', response = @response::jsonb, completed_at = now()
            WHERE turn_id = @turn_id AND n = @n AND status = 'pending'
            """);
        cmd.Parameters.AddWithValue("turn_id", turnId);
        cmd.Parameters.AddWithValue("n", n);
        cmd.Parameters.Add(AgentSql.Jsonb("response", responseJson));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Marks a pending step abandoned or unknown. Returns false if the step is not pending.</summary>
    public async Task<bool> MarkUnfinishedAsync(Guid turnId, int n, TurnStepStatus status, CancellationToken ct = default)
    {
        if (status is not (TurnStepStatus.Abandoned or TurnStepStatus.Unknown))
            throw new ArgumentException("Only abandoned or unknown can mark an unfinished step.", nameof(status));

        await using var cmd = dataSource.CreateCommand("""
            UPDATE turn_step SET status = @status
            WHERE turn_id = @turn_id AND n = @n AND status = 'pending'
            """);
        cmd.Parameters.AddWithValue("turn_id", turnId);
        cmd.Parameters.AddWithValue("n", n);
        cmd.Parameters.AddWithValue("status", AgentSql.ToDb(status));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>All steps of a turn in step order.</summary>
    public async Task<IReadOnlyList<TurnStepRecord>> ListAsync(Guid turnId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT turn_id, n, kind, status, request_hash, response::text, idempotent, started_at, completed_at
            FROM turn_step WHERE turn_id = @turn_id ORDER BY n
            """);
        cmd.Parameters.AddWithValue("turn_id", turnId);
        var result = new List<TurnStepRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TurnStepRecord(
                reader.GetGuid(0),
                reader.GetInt32(1),
                AgentSql.FromDb<TurnStepKind>(reader.GetString(2)),
                AgentSql.FromDb<TurnStepStatus>(reader.GetString(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return result;
    }
}
