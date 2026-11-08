using Bazaar.Contracts.Kanban;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The board snapshot the client binds to: blocker ids, blocker titles, owner and string states (doc 12).</summary>
public sealed class BoardSnapshotTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<(Guid ProjectId, Guid GcId)> NewBoardAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        return (Guid.CreateVersion7(), gcId);
    }

    private async Task<Guid> AddTaskAsync(Guid projectId, Guid gcId, string title, TaskState state, Guid? owner = null)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, projectId, title, state, owner, TaskGate.None, gcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    [Fact]
    public async Task A_card_lists_its_unfinished_blockers_with_titles_and_string_state()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var agent = Guid.CreateVersion7();
        var blocker = await AddTaskAsync(project, gc, "Write the schema", TaskState.Ready);
        var blocked = await AddTaskAsync(project, gc, "Wire the API", TaskState.Backlog, agent);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(blocked, blocker, _owner, Ct);

        var snapshot = await service.GetBoardSnapshotAsync(project, Ct);

        var card = Assert.Single(snapshot.Cards, c => c.Id == blocked);
        Assert.Equal("Wire the API", card.Title);
        Assert.Equal("backlog", card.State);
        Assert.Equal(agent, card.OwnerAgentId);
        Assert.Equal([blocker], card.UnfinishedBlockerIds);
        Assert.Equal(["Write the schema"], card.BlockerTitles);
    }

    [Fact]
    public async Task A_blocker_that_is_done_is_not_listed()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var blocker = await AddTaskAsync(project, gc, "Finished work", TaskState.Done);
        var blocked = await AddTaskAsync(project, gc, "Next", TaskState.Ready);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(blocked, blocker, _owner, Ct);

        var snapshot = await service.GetBoardSnapshotAsync(project, Ct);

        var card = Assert.Single(snapshot.Cards, c => c.Id == blocked);
        Assert.Empty(card.UnfinishedBlockerIds);
        Assert.Empty(card.BlockerTitles);
        Assert.Null(card.OwnerAgentId);
    }

    [Fact]
    public async Task The_snapshot_covers_only_the_requested_project()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var (other, otherGc) = await NewBoardAsync();
        var mine = await AddTaskAsync(project, gc, "Mine", TaskState.Ready);
        var theirs = await AddTaskAsync(other, otherGc, "Theirs", TaskState.Ready);
        var service = new TaskService(db.DataSource);

        var snapshot = await service.GetBoardSnapshotAsync(project, Ct);

        Assert.Equal(project, snapshot.ProjectId);
        Assert.Contains(snapshot.Cards, c => c.Id == mine);
        Assert.DoesNotContain(snapshot.Cards, c => c.Id == theirs);
    }
}
