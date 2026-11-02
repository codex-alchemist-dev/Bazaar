using Npgsql;

namespace Bazaar.Server.Kanban.Data;

/// <summary>SQL-first access to <c>task_edges</c> (doc 12). Cycle and duplicate checks are the graph service's job, not this repository's.</summary>
public sealed class TaskEdgeRepository(NpgsqlDataSource dataSource)
{
    private const string _insertSql = """
        INSERT INTO task_edges (blocker_id, blocked_id, created_by) VALUES (@blocker_id, @blocked_id, @created_by)
        """;

    private const string _deleteSql = "DELETE FROM task_edges WHERE blocker_id = @blocker_id AND blocked_id = @blocked_id";

    /// <summary>Every blocked-by edge whose blocked task belongs to <paramref name="projectId"/>. Pass <paramref name="tx"/> to read under its locks.</summary>
    public async Task<IReadOnlyList<TaskEdge>> ListForProjectAsync(Guid projectId, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = KanbanCommands.Create(dataSource, tx, """
            SELECT e.blocker_id, e.blocked_id
            FROM task_edges e
            JOIN tasks t ON t.id = e.blocked_id
            WHERE t.project_id = @project_id
            ORDER BY e.blocker_id, e.blocked_id
            """);
        cmd.Parameters.AddWithValue("project_id", projectId);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>The tasks that block <paramref name="blockedId"/>.</summary>
    public async Task<IReadOnlyList<TaskEdge>> ListBlockersAsync(Guid blockedId, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = KanbanCommands.Create(dataSource, tx,
            "SELECT blocker_id, blocked_id FROM task_edges WHERE blocked_id = @blocked_id ORDER BY blocker_id");
        cmd.Parameters.AddWithValue("blocked_id", blockedId);
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Adds one edge. A duplicate or self-edge fails on the primary key or the CHECK constraint.</summary>
    public async Task InsertAsync(NpgsqlTransaction tx, TaskEdge edge, Guid createdBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _insertSql);
        cmd.Parameters.AddWithValue("blocker_id", edge.BlockerId);
        cmd.Parameters.AddWithValue("blocked_id", edge.BlockedId);
        cmd.Parameters.AddWithValue("created_by", createdBy);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes one edge. Returns false when it was not there.</summary>
    public async Task<bool> DeleteAsync(NpgsqlTransaction tx, TaskEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _deleteSql);
        cmd.Parameters.AddWithValue("blocker_id", edge.BlockerId);
        cmd.Parameters.AddWithValue("blocked_id", edge.BlockedId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<TaskEdge>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var result = new List<TaskEdge>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TaskEdge(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return result;
    }
}
