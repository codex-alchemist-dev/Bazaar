using Npgsql;

namespace Bazaar.RyuseiFuda.Kanban.Data;

/// <summary>SQL-first read of the board list (doc 12, "Board names"): named boards plus boards that still hold non-withdrawn tasks.</summary>
public sealed class BoardDirectoryRepository(NpgsqlDataSource dataSource) : IBoardDirectoryReader
{
    private const string _listSql = """
        SELECT b.project_id, p.name
        FROM (SELECT project_id FROM kanban_project
              UNION
              SELECT project_id FROM tasks WHERE state <> 'withdrawn') b
        LEFT JOIN kanban_project p ON p.project_id = b.project_id
        ORDER BY p.name NULLS LAST, b.project_id
        """;

    public async Task<IReadOnlyList<BoardListing>> ListBoardsAsync(CancellationToken ct = default)
    {
        await using var cmd = KanbanCommands.Create(dataSource, null, _listSql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var boards = new List<BoardListing>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            boards.Add(new BoardListing(id, reader.IsDBNull(1) ? DefaultName(id) : reader.GetString(1)));
        }

        return boards;
    }

    /// <summary>The name shown for a board with no name row.</summary>
    public static string DefaultName(Guid boardId) => "Project " + boardId.ToString("N")[..8];
}
