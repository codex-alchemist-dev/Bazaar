using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Kanban.Data;

/// <summary>Append-only access to <c>task_events</c> (doc 12, Events). Rows are written in the same transaction as the change they describe.</summary>
public sealed class TaskEventRepository(NpgsqlDataSource dataSource)
{
    private const string _appendSql = """
        INSERT INTO task_events (task_id, kind, actor, payload) VALUES (@task_id, @kind, @actor, @payload::jsonb)
        RETURNING seq
        """;

    /// <summary>Appends one event and returns its seq. Nothing here can update or delete an event.</summary>
    public async Task<long> AppendAsync(NpgsqlTransaction tx, NewTaskEvent taskEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(taskEvent);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _appendSql);
        cmd.Parameters.AddWithValue("task_id", taskEvent.TaskId);
        cmd.Parameters.AddWithValue("kind", taskEvent.Kind);
        cmd.Parameters.AddWithValue("actor", taskEvent.Actor);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Text) { Value = taskEvent.PayloadJson });
        return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
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
}
