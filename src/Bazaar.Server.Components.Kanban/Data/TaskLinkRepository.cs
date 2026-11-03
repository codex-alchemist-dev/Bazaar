using Npgsql;

namespace Bazaar.Server.Kanban.Data;

/// <summary>SQL-first access to <c>task_links</c> (doc 12). Existence checks against PRs, commits, GCs and Vault documents belong to the link service (KB-later).</summary>
public sealed class TaskLinkRepository(NpgsqlDataSource dataSource)
{
    private const string _insertSql = """
        INSERT INTO task_links (task_id, kind, ref) VALUES (@task_id, @kind, @ref)
        ON CONFLICT (task_id, kind, ref) DO NOTHING
        """;

    private const string _deleteSql = "DELETE FROM task_links WHERE task_id = @task_id AND kind = @kind AND ref = @ref";

    /// <summary>The links of one task, in kind then reference order.</summary>
    public async Task<IReadOnlyList<TaskLink>> ListAsync(Guid taskId, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = KanbanCommands.Create(dataSource, tx,
            "SELECT task_id, kind, ref FROM task_links WHERE task_id = @task_id ORDER BY kind, ref");
        cmd.Parameters.AddWithValue("task_id", taskId);
        var result = new List<TaskLink>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TaskLink(reader.GetGuid(0), KanbanText.ParseLinkKind(reader.GetString(1)), reader.GetString(2)));
        }

        return result;
    }

    /// <summary>Tasks that carry a link of this kind and reference, oldest first.</summary>
    public async Task<IReadOnlyList<Guid>> FindTaskIdsAsync(TaskLinkKind kind, string reference, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT task_id FROM task_links l
            JOIN tasks t ON t.id = l.task_id
            WHERE l.kind = @kind AND l.ref = @ref
            ORDER BY t.created_at, t.id
            """);
        cmd.Parameters.AddWithValue("kind", KanbanText.ToSql(kind));
        cmd.Parameters.AddWithValue("ref", reference);
        var result = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    /// <summary>Adds a link. Returns false when the same link was already there.</summary>
    public async Task<bool> InsertAsync(NpgsqlTransaction tx, TaskLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _insertSql);
        cmd.Parameters.AddWithValue("task_id", link.TaskId);
        cmd.Parameters.AddWithValue("kind", KanbanText.ToSql(link.Kind));
        cmd.Parameters.AddWithValue("ref", link.Ref);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Removes a link. Returns false when it was not there.</summary>
    public async Task<bool> DeleteAsync(NpgsqlTransaction tx, TaskLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _deleteSql);
        cmd.Parameters.AddWithValue("task_id", link.TaskId);
        cmd.Parameters.AddWithValue("kind", KanbanText.ToSql(link.Kind));
        cmd.Parameters.AddWithValue("ref", link.Ref);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
