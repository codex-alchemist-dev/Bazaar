using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>AddLinkAsync on real PostgreSQL: validation before insert, GC existence and idempotent insert (doc 12, Links).</summary>
public sealed class TaskLinkAddTests(KanbanDb db) : IClassFixture<KanbanDb>
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

    private async Task<Guid> AddTaskAsync((Guid ProjectId, Guid GcId) board)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, board.ProjectId, "Task", TaskState.InProgress, null, TaskGate.None, board.GcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private async Task<IReadOnlyList<TaskLink>> LinksAsync(Guid taskId) =>
        await new TaskLinkRepository(db.DataSource).ListAsync(taskId, null, Ct);

    private async Task<long> LinkCountAsync(Guid taskId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM task_links WHERE task_id = $1");
        cmd.Parameters.AddWithValue(taskId);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<long> EventTotalAsync()
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM task_events");
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_valid_pr_link_is_inserted()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);

        var result = await service.AddLinkAsync(task, TaskLinkKind.Pr, "owner-x/repo#42", _owner, Ct);

        Assert.Equal(LinkStatus.Added, result.Status);
        Assert.Null(result.Refusal);
        var link = Assert.Single(await LinksAsync(task));
        Assert.Equal(new TaskLink(task, TaskLinkKind.Pr, "owner-x/repo#42"), link);
    }

    [Fact]
    public async Task Adding_the_same_link_twice_reports_already_present_and_keeps_one_row()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);

        await service.AddLinkAsync(task, TaskLinkKind.Commit, "abcdef1234", _owner, Ct);
        var again = await service.AddLinkAsync(task, TaskLinkKind.Commit, "abcdef1234", _owner, Ct);

        Assert.Equal(LinkStatus.AlreadyPresent, again.Status);
        Assert.Equal(1, await LinkCountAsync(task));
    }

    [Fact]
    public async Task A_malformed_pr_link_is_refused_and_nothing_is_written()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);

        var result = await service.AddLinkAsync(task, TaskLinkKind.Pr, "owner/repo#0", _owner, Ct);

        Assert.Equal(LinkStatus.Refused, result.Status);
        Assert.NotNull(result.Refusal);
        Assert.Equal(TaskLinkKind.Pr, result.Refusal.Kind);
        Assert.Equal(LinkRefusalReason.Malformed, result.Refusal.Reason);
        Assert.Equal(0, await LinkCountAsync(task));
    }

    [Fact]
    public async Task A_malformed_commit_link_is_refused_and_nothing_is_written()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);

        var result = await service.AddLinkAsync(task, TaskLinkKind.Commit, "ABCDEF1", _owner, Ct);

        Assert.Equal(LinkStatus.Refused, result.Status);
        Assert.Equal(LinkRefusalReason.Malformed, result.Refusal!.Reason);
        Assert.Equal(0, await LinkCountAsync(task));
    }

    [Fact]
    public async Task A_vault_link_with_parent_segments_is_refused()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);

        var result = await service.AddLinkAsync(task, TaskLinkKind.VaultDoc, "../outside.md", _owner, Ct);

        Assert.Equal(LinkStatus.Refused, result.Status);
        Assert.Equal(LinkRefusalReason.Malformed, result.Refusal!.Reason);
        Assert.Equal(0, await LinkCountAsync(task));
    }

    [Fact]
    public async Task A_valid_vault_link_is_inserted_without_reading_the_vault()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);

        var result = await service.AddLinkAsync(task, TaskLinkKind.VaultDoc, "notes/plan.md", _owner, Ct);

        Assert.Equal(LinkStatus.Added, result.Status);
        Assert.Equal(1, await LinkCountAsync(task));
    }

    [Fact]
    public async Task A_gc_link_to_an_existing_gc_is_inserted()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board);
        var service = new TaskService(db.DataSource);

        var result = await service.AddLinkAsync(task, TaskLinkKind.Gc, board.GcId.ToString("D"), _owner, Ct);

        Assert.Equal(LinkStatus.Added, result.Status);
        Assert.Equal(1, await LinkCountAsync(task));
    }

    [Fact]
    public async Task A_gc_link_to_an_unknown_gc_is_refused_by_the_gc_check()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);
        var unknown = Guid.CreateVersion7().ToString("D");

        var result = await service.AddLinkAsync(task, TaskLinkKind.Gc, unknown, _owner, Ct);

        Assert.Equal(LinkStatus.Refused, result.Status);
        Assert.Equal(TaskLinkKind.Gc, result.Refusal!.Kind);
        Assert.Equal(LinkRefusalReason.UnknownGc, result.Refusal.Reason);
        Assert.Equal(0, await LinkCountAsync(task));
    }

    [Fact]
    public async Task A_gc_link_is_checked_through_the_injected_existence_check()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var fake = new FakeGcExistence(known: Guid.CreateVersion7());
        var service = new TaskService(db.DataSource, gcExistence: fake);
        var gcId = fake.Known.ToString("D");

        var refused = await service.AddLinkAsync(task, TaskLinkKind.Gc, Guid.CreateVersion7().ToString("D"), _owner, Ct);
        var added = await service.AddLinkAsync(task, TaskLinkKind.Gc, gcId, _owner, Ct);

        Assert.Equal(LinkRefusalReason.UnknownGc, refused.Refusal!.Reason);
        Assert.Equal(LinkStatus.Added, added.Status);
        Assert.Equal(1, await LinkCountAsync(task));
        // The refused unknown GC and the known GC each ran the check once.
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task A_malformed_gc_reference_is_refused_before_the_gc_check_runs()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var fake = new FakeGcExistence(known: Guid.CreateVersion7());
        var service = new TaskService(db.DataSource, gcExistence: fake);

        var result = await service.AddLinkAsync(task, TaskLinkKind.Gc, "not-a-guid", _owner, Ct);

        Assert.Equal(LinkRefusalReason.Malformed, result.Refusal!.Reason);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task A_link_to_an_unknown_task_reports_unknown_task()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);

        var result = await service.AddLinkAsync(Guid.CreateVersion7(), TaskLinkKind.Commit, "abcdef1", _owner, Ct);

        Assert.Equal(LinkStatus.UnknownTask, result.Status);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public async Task A_refused_link_writes_no_task_event()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(await NewBoardAsync());
        var service = new TaskService(db.DataSource);
        var before = await EventTotalAsync();

        await service.AddLinkAsync(task, TaskLinkKind.Pr, "bad", _owner, Ct);
        await service.AddLinkAsync(task, TaskLinkKind.Commit, "abcdef1", _owner, Ct);

        // No link event exists (task_events does not allow one yet), so a successful add writes no event either.
        Assert.Equal(before, await EventTotalAsync());
    }

    private sealed class FakeGcExistence(Guid known) : IGcExistence
    {
        public Guid Known { get; } = known;

        public int Calls { get; private set; }

        public Task<bool> ExistsAsync(Guid gcId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(gcId == Known);
        }
    }
}
