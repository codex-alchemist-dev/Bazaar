using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

public sealed class BoardDirectoryNameTests
{
    [Fact]
    public void An_unnamed_board_is_shown_as_project_and_the_first_eight_hex_digits()
    {
        var id = Guid.Parse("0a1b2c3d-0000-7000-8000-000000000000");

        Assert.Equal("Project 0a1b2c3d", BoardDirectoryRepository.DefaultName(id));
    }
}

/// <summary>The board list on real PostgreSQL (doc 12, "Board names").</summary>
public sealed class BoardDirectoryTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_named_boards_and_boards_with_live_tasks_but_not_withdrawn_only_boards()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var named = Guid.CreateVersion7();
        var unnamed = Guid.CreateVersion7();
        var withdrawnOnly = Guid.CreateVersion7();
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO kanban_project (project_id, name) VALUES ($1, 'Alpha')"))
        {
            cmd.Parameters.AddWithValue(named);
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var tasks = new TaskRepository(db.DataSource);
        foreach (var (board, state) in new[] { (unnamed, TaskState.Backlog), (withdrawnOnly, TaskState.Withdrawn) })
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
            await using var tx = await conn.BeginTransactionAsync(Ct);
            await tasks.InsertAsync(tx, new NewTask(Guid.CreateVersion7(), board, "T", state, null, TaskGate.None, gcId, null, null), Ct);
            await tx.CommitAsync(Ct);
        }

        var boards = await new BoardDirectoryRepository(db.DataSource).ListBoardsAsync(Ct);

        Assert.Contains(new BoardListing(named, "Alpha"), boards);
        Assert.Contains(new BoardListing(unnamed, BoardDirectoryRepository.DefaultName(unnamed)), boards);
        Assert.DoesNotContain(boards, b => b.BoardId == withdrawnOnly);
    }
}
