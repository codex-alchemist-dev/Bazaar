using Npgsql;

namespace Bazaar.Server.Kanban.Data;

/// <summary>
/// SQL-first access to <c>tasks</c> (doc 12). Writes take the caller's transaction so the task service can lock, check
/// and write in one unit. Nothing here checks transition rules; that is the task service's job.
/// </summary>
public sealed class TaskRepository(NpgsqlDataSource dataSource)
{
    private const string _columns = """
        id, project_id, title, state, owner_agent_id, gate, origin_gc_id, origin_message_id,
        due_at, review_approved_by, review_approved_at, version, created_at, updated_at
        """;

    private const string _insertSql = """
        INSERT INTO tasks (id, project_id, title, state, owner_agent_id, gate, origin_gc_id, origin_message_id, due_at)
        VALUES (@id, @project_id, @title, @state, @owner_agent_id, @gate, @origin_gc_id, @origin_message_id, @due_at)
        RETURNING
        """;

    // Writes every mutable column from the record and bumps the version only when the expected version matches.
    private const string _updateSql = """
        UPDATE tasks
        SET state = @state, owner_agent_id = @owner_agent_id, gate = @gate, due_at = @due_at,
            review_approved_by = @review_approved_by, review_approved_at = @review_approved_at,
            version = version + 1, updated_at = now()
        WHERE id = @id AND version = @expected_version
        """;

    /// <summary>Reads one task without locking it.</summary>
    public async Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM tasks WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Reads one task and holds a row lock on it until <paramref name="tx"/> ends (<c>SELECT ... FOR UPDATE</c>).</summary>
    public async Task<TaskRecord?> GetForUpdateAsync(NpgsqlTransaction tx, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, $"SELECT {_columns} FROM tasks WHERE id = @id FOR UPDATE");
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <summary>Lists a project's tasks, optionally in one column, oldest first.</summary>
    public async Task<IReadOnlyList<TaskRecord>> ListByProjectAsync(Guid projectId, TaskState? state = null, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = KanbanCommands.Create(dataSource, tx, $"""
            SELECT {_columns} FROM tasks
            WHERE project_id = @project_id AND (@state::text IS NULL OR state = @state)
            ORDER BY created_at, id
            """);
        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("state", KanbanCommands.Nullable(state is null ? null : KanbanText.ToSql(state.Value)));
        var result = new List<TaskRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    /// <summary>Inserts a task at version 1 and returns the stored row.</summary>
    public async Task<TaskRecord> InsertAsync(NpgsqlTransaction tx, NewTask task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(task);
        await using var cmd = KanbanCommands.Create(dataSource, tx, $"{_insertSql} {_columns}");
        cmd.Parameters.AddWithValue("id", task.Id);
        cmd.Parameters.AddWithValue("project_id", task.ProjectId);
        cmd.Parameters.AddWithValue("title", task.Title);
        cmd.Parameters.AddWithValue("state", KanbanText.ToSql(task.State));
        cmd.Parameters.AddWithValue("owner_agent_id", KanbanCommands.Nullable(task.OwnerAgentId));
        cmd.Parameters.AddWithValue("gate", KanbanText.ToSql(task.Gate));
        cmd.Parameters.AddWithValue("origin_gc_id", task.OriginGcId);
        cmd.Parameters.AddWithValue("origin_message_id", KanbanCommands.Nullable(task.OriginMessageId));
        cmd.Parameters.AddWithValue("due_at", KanbanCommands.Nullable(task.DueAt));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return Read(reader);
    }

    /// <summary>
    /// Writes the mutable columns of <paramref name="next"/> if the stored version is still <paramref name="expectedVersion"/>.
    /// Returns false, and changes nothing, when another writer got there first. On success the version is expectedVersion + 1.
    /// </summary>
    public async Task<bool> UpdateAsync(NpgsqlTransaction tx, TaskRecord next, long expectedVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(next);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _updateSql);
        cmd.Parameters.AddWithValue("id", next.Id);
        cmd.Parameters.AddWithValue("expected_version", expectedVersion);
        cmd.Parameters.AddWithValue("state", KanbanText.ToSql(next.State));
        cmd.Parameters.AddWithValue("owner_agent_id", KanbanCommands.Nullable(next.OwnerAgentId));
        cmd.Parameters.AddWithValue("gate", KanbanText.ToSql(next.Gate));
        cmd.Parameters.AddWithValue("due_at", KanbanCommands.Nullable(next.DueAt));
        cmd.Parameters.AddWithValue("review_approved_by", KanbanCommands.Nullable(next.ReviewApprovedBy));
        cmd.Parameters.AddWithValue("review_approved_at", KanbanCommands.Nullable(next.ReviewApprovedAt));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static TaskRecord Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        KanbanText.ParseState(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetGuid(4),
        KanbanText.ParseGate(r.GetString(5)),
        r.GetGuid(6),
        r.IsDBNull(7) ? null : r.GetGuid(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetGuid(9),
        r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        r.GetInt64(11),
        r.GetFieldValue<DateTimeOffset>(12),
        r.GetFieldValue<DateTimeOffset>(13));
}
