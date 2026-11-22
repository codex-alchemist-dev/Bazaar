using Npgsql;
using NpgsqlTypes;

namespace Bazaar.RyuseiFuda.Kanban.Data;

/// <summary>Append-only access to <c>task_events</c> (doc 12, Events). Rows are written in the same transaction as the change they describe.</summary>
public sealed class TaskEventRepository(NpgsqlDataSource dataSource)
{
    private const string _appendSql = """
        INSERT INTO task_events (task_id, kind, actor, payload) VALUES (@task_id, @kind, @actor, @payload::jsonb)
        RETURNING seq
        """;

    /// <summary>
    /// Channel for task-event wake hints (doc 12, WP8). A notification is a hint only: consumers read the event rows.
    /// </summary>
    public const string NotifyChannel = "kanban_event";

    /// <summary>
    /// Appends one event and returns its seq, then sends a <c>NOTIFY</c> on <see cref="NotifyChannel"/> inside the same
    /// transaction. PostgreSQL delivers the notification only when the transaction commits, so a rolled-back change is
    /// never announced. Nothing here can update or delete an event.
    /// </summary>
    public async Task<long> AppendAsync(NpgsqlTransaction tx, NewTaskEvent taskEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(taskEvent);
        long seq;
        await using (var cmd = KanbanCommands.Create(dataSource, tx, _appendSql))
        {
            cmd.Parameters.AddWithValue("task_id", taskEvent.TaskId);
            cmd.Parameters.AddWithValue("kind", taskEvent.Kind);
            cmd.Parameters.AddWithValue("actor", taskEvent.Actor);
            cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Text) { Value = taskEvent.PayloadJson });
            seq = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        await using var notify = KanbanCommands.Create(dataSource, tx, "SELECT pg_notify(@channel, @payload)");
        notify.Parameters.AddWithValue("channel", NotifyChannel);
        notify.Parameters.AddWithValue("payload", $"{{\"task_id\":\"{taskEvent.TaskId:D}\",\"seq\":{seq},\"kind\":\"{taskEvent.Kind}\"}}");
        await notify.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return seq;
    }

    /// <summary>Events of one task with seq greater than <paramref name="afterSeq"/>, oldest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<TaskEventRecord>> ListAsync(Guid taskId, long afterSeq = 0, int limit = 100, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = dataSource.CreateCommand("""
            SELECT seq, task_id, kind, actor, payload::text, at FROM task_events
            WHERE task_id = @task_id AND seq > @after_seq
            ORDER BY seq
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("task_id", taskId);
        cmd.Parameters.AddWithValue("after_seq", afterSeq);
        cmd.Parameters.AddWithValue("limit", limit);
        var result = new List<TaskEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TaskEventRecord(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return result;
    }
    /// <summary>
    /// Column moves (<c>task.transitioned</c>) of the project's tasks in <c>[fromInclusive, toExclusive)</c>, oldest first. Reads the
    /// <c>from</c> and <c>to</c> columns the task service writes into the payload. Used by project health (doc 25).
    /// </summary>
    public async Task<IReadOnlyList<TaskTransitionFact>> ListTransitionsAsync(Guid projectId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT e.task_id, e.payload ->> 'from', e.payload ->> 'to', e.at
            FROM task_events e
            JOIN tasks t ON t.id = e.task_id
            WHERE t.project_id = @project_id AND e.kind = @kind AND e.at >= @from AND e.at < @to
            ORDER BY e.seq
            """);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("kind", TaskEventKinds.Transitioned);
        cmd.Parameters.AddWithValue("from", fromInclusive);
        cmd.Parameters.AddWithValue("to", toExclusive);
        var result = new List<TaskTransitionFact>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2))
                continue;
            result.Add(new TaskTransitionFact(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return result;
    }
}
