using Npgsql;

namespace Bazaar.Server.Kanban.Data;

/// <summary>
/// Reads for the deadline scanner (doc 12, WP11). A task is due-passed for one due time once its <c>due_at</c> is at or before
/// now, it is not Done, and no <c>task.due_passed</c> event carries that same due time. The <c>due_at</c> in the payload is
/// compared as a timestamp, so each (task, due_at) pair is announced once.
/// </summary>
public sealed class TaskDeadlineRepository(NpgsqlDataSource dataSource)
{
    // Candidate scan, oldest due date first. A plain read: the handler re-checks each row under lock before it writes.
    private const string _candidatesSql = """
        SELECT t.id
        FROM tasks AS t
        WHERE t.due_at IS NOT NULL
          AND t.due_at <= now()
          AND t.state <> 'done'
          AND NOT EXISTS (
              SELECT 1 FROM task_events AS e
              WHERE e.task_id = t.id AND e.kind = 'task.due_passed'
                AND (e.payload->>'due_at')::timestamptz = t.due_at)
        ORDER BY t.due_at, t.id
        LIMIT @batch_size
        """;

    // Run inside the transaction after the task row is locked. This is a new statement, so under READ COMMITTED it sees any
    // due_passed event that a concurrent scanner committed while this one waited for the lock.
    private const string _unnotifiedSql = """
        SELECT t.due_at
        FROM tasks AS t
        WHERE t.id = @id
          AND t.due_at IS NOT NULL
          AND t.due_at <= now()
          AND t.state <> 'done'
          AND NOT EXISTS (
              SELECT 1 FROM task_events AS e
              WHERE e.task_id = t.id AND e.kind = 'task.due_passed'
                AND (e.payload->>'due_at')::timestamptz = t.due_at)
        """;

    /// <summary>Returns up to <paramref name="batchSize"/> task ids that may need a <c>task.due_passed</c> event.</summary>
    public async Task<IReadOnlyList<Guid>> ListCandidatesAsync(int batchSize, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        await using var cmd = dataSource.CreateCommand(_candidatesSql);
        cmd.Parameters.AddWithValue("batch_size", batchSize);
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>
    /// Returns the task's due time if it is still due-passed and not yet announced for that time, or null. Call it inside the
    /// transaction that holds the task's row lock.
    /// </summary>
    public async Task<DateTimeOffset?> GetUnnotifiedDueAtAsync(NpgsqlTransaction tx, Guid taskId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _unnotifiedSql);
        cmd.Parameters.AddWithValue("id", taskId);
        // A reader, not ExecuteScalar: scalar reads of timestamptz come back as DateTime, not DateTimeOffset.
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }
}
