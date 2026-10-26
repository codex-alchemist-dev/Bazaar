using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The task service on real PostgreSQL: locked transitions, version checks, guard, auto-ready and cycles (doc 12, WP4-5).</summary>
public sealed class TaskServiceTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static TaskActor Agent() => new(TaskActorKind.Agent, Guid.CreateVersion7());

    private sealed record Board(Guid ProjectId, Guid GcId);

    private async Task<Board> NewBoardAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        return new Board(Guid.CreateVersion7(), gcId);
    }

    private async Task<Guid> AddTaskAsync(Board board, TaskState state = TaskState.Backlog, string title = "Task")
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, board.ProjectId, title, state, null, TaskGate.None, board.GcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private async Task<TaskRecord> ReadAsync(Guid id) => (await new TaskRepository(db.DataSource).GetAsync(id, Ct))!;

    private async Task<long> EventCountAsync(Guid taskId, string kind)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM task_events WHERE task_id = $1 AND kind = $2");
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(kind);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task LinkAsync(Guid taskId, TaskLinkKind kind, string reference)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskLinkRepository(db.DataSource).InsertAsync(tx, new TaskLink(taskId, kind, reference), Ct);
        await tx.CommitAsync(Ct);
    }

    [Fact]
    public async Task Claim_moves_ready_to_in_progress_and_sets_the_owner()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);
        var agent = Agent();

        var result = await service.TransitionAsync(task, TaskState.InProgress, agent, null, expectedVersion: 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Equal(2, result.Version);
        var row = await ReadAsync(task);
        Assert.Equal(TaskState.InProgress, row.State);
        Assert.Equal(agent.Id, row.OwnerAgentId);
        Assert.Equal(1, await EventCountAsync(task, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task Two_agents_claiming_one_ready_task_with_the_same_version_let_exactly_one_win()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);

        var results = await Task.WhenAll(
            service.TransitionAsync(task, TaskState.InProgress, Agent(), null, 1, Ct),
            service.TransitionAsync(task, TaskState.InProgress, Agent(), null, 1, Ct));

        Assert.Equal(1, results.Count(r => r.Status == TransitionStatus.Applied));
        var loser = results.Single(r => r.Status == TransitionStatus.Conflict);
        Assert.Equal(2, loser.Version);
        Assert.Equal(1, await EventCountAsync(task, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task A_stale_version_changes_nothing_and_writes_no_event()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);

        var result = await service.TransitionAsync(task, TaskState.InProgress, Agent(), null, expectedVersion: 9, Ct);

        Assert.Equal(TransitionStatus.Conflict, result.Status);
        Assert.Equal(1, result.Version);
        Assert.Equal(TaskState.Ready, (await ReadAsync(task)).State);
        Assert.Equal(0, await EventCountAsync(task, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task Starting_is_refused_while_a_blocker_is_unfinished_and_records_the_refusal()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var blocker = await AddTaskAsync(board, TaskState.Ready);
        var blocked = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);
        Assert.Equal(EdgeStatus.Added, (await service.AddBlockerAsync(blocked, blocker, _owner, Ct)).Status);

        var result = await service.TransitionAsync(blocked, TaskState.InProgress, Agent(), null, 1, Ct);

        Assert.Equal(TransitionStatus.Refused, result.Status);
        Assert.Equal(TransitionOutcome.BlockersUnfinished, result.Outcome);
        Assert.Equal([blocker], result.UnfinishedBlockers);
        Assert.Equal(TaskState.Ready, (await ReadAsync(blocked)).State);
        Assert.Equal(1, await EventCountAsync(blocked, TaskEventKinds.GuardRefused));
        Assert.Equal(0, await EventCountAsync(blocked, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task Dispatch_check_reports_the_same_blockers_without_writing()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var blocker = await AddTaskAsync(board, TaskState.InProgress);
        var blocked = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(blocked, blocker, _owner, Ct);

        var check = await service.CheckStartAsync(blocked, Ct);

        Assert.False(check.Allowed);
        Assert.Equal([blocker], check.UnfinishedBlockers);
        Assert.Equal(0, await EventCountAsync(blocked, TaskEventKinds.GuardRefused));
    }

    [Fact]
    public async Task Completing_the_last_blocker_auto_readies_the_dependent_in_the_same_transaction()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var first = await AddTaskAsync(board, TaskState.Ready);
        var dependent = await AddTaskAsync(board, TaskState.Backlog);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(dependent, first, _owner, Ct);

        var worker = Agent();
        Assert.Equal(TransitionStatus.Applied, (await service.TransitionAsync(first, TaskState.InProgress, worker, null, 1, Ct)).Status);
        await LinkAsync(first, TaskLinkKind.Commit, "abc123");
        Assert.Equal(TransitionStatus.Applied, (await service.TransitionAsync(first, TaskState.Review, worker, null, 2, Ct)).Status);
        var done = await service.TransitionAsync(first, TaskState.Done, _owner, null, 3, Ct);

        Assert.Equal(TransitionStatus.Applied, done.Status);
        Assert.Equal(TaskState.Ready, (await ReadAsync(dependent)).State);
        Assert.Equal(1, await EventCountAsync(dependent, TaskEventKinds.Unblocked));
        Assert.Equal(1, await EventCountAsync(dependent, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task Auto_ready_is_off_when_the_setting_is_off()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var first = await AddTaskAsync(board, TaskState.Review);
        var dependent = await AddTaskAsync(board, TaskState.Backlog);
        var service = new TaskService(db.DataSource, new TaskServiceOptions { AutoReady = false });
        await service.AddBlockerAsync(dependent, first, _owner, Ct);

        await service.TransitionAsync(first, TaskState.Done, _owner, null, 1, Ct);

        Assert.Equal(TaskState.Backlog, (await ReadAsync(dependent)).State);
    }

    [Fact]
    public async Task Reopening_done_marks_dependents_blocked_and_keeps_their_state()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var first = await AddTaskAsync(board, TaskState.Review);
        var dependent = await AddTaskAsync(board, TaskState.Backlog);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(dependent, first, _owner, Ct);
        await service.TransitionAsync(first, TaskState.Done, _owner, null, 1, Ct);
        Assert.Equal(TaskState.Ready, (await ReadAsync(dependent)).State);

        var reopened = await service.TransitionAsync(first, TaskState.Review, _owner, "found a regression", 2, Ct);

        Assert.Equal(TransitionStatus.Applied, reopened.Status);
        Assert.Equal(TaskState.Ready, (await ReadAsync(dependent)).State);
        // Blocked once when the edge was added, and again when the reopen removed the Done blocker.
        Assert.Equal(2, await EventCountAsync(dependent, TaskEventKinds.Blocked));
    }

    [Fact]
    public async Task A_pr_linked_card_cannot_reach_done_by_a_move()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Review);
        await LinkAsync(task, TaskLinkKind.Pr, "codex-alchemist-dev/Bazaar#1");
        var service = new TaskService(db.DataSource);

        var result = await service.TransitionAsync(task, TaskState.Done, _owner, null, 1, Ct);

        Assert.Equal(TransitionStatus.Refused, result.Status);
        Assert.Equal(TransitionOutcome.PrNotMerged, result.Outcome);
        Assert.Equal(TaskState.Review, (await ReadAsync(task)).State);
    }

    [Fact]
    public async Task Moves_outside_the_table_are_invalid_not_refused()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Backlog);
        var service = new TaskService(db.DataSource);

        var result = await service.TransitionAsync(task, TaskState.Done, _owner, null, 1, Ct);

        Assert.Equal(TransitionStatus.Invalid, result.Status);
        Assert.Equal(TransitionOutcome.NotATransition, result.Outcome);
    }

    [Fact]
    public async Task Two_concurrent_edges_that_close_a_cycle_let_exactly_one_in()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var a = await AddTaskAsync(board);
        var b = await AddTaskAsync(board);
        var service = new TaskService(db.DataSource);

        var results = await Task.WhenAll(
            service.AddBlockerAsync(b, a, _owner, Ct),
            service.AddBlockerAsync(a, b, _owner, Ct));

        Assert.Equal(1, results.Count(r => r.Status == EdgeStatus.Added));
        Assert.Equal(1, results.Count(r => r.Status == EdgeStatus.Cycle));
    }

    [Fact]
    public async Task A_three_node_cycle_is_refused_and_the_path_is_returned()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var a = await AddTaskAsync(board);
        var b = await AddTaskAsync(board);
        var c = await AddTaskAsync(board);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(b, a, _owner, Ct);
        await service.AddBlockerAsync(c, b, _owner, Ct);

        var closing = await service.AddBlockerAsync(a, c, _owner, Ct);

        Assert.Equal(EdgeStatus.Cycle, closing.Status);
        Assert.Equal([a, b, c], closing.CyclePath);
    }

    [Fact]
    public async Task Edges_across_projects_and_missing_tasks_are_refused()
    {
        SkipWithoutDatabase();
        var one = await NewBoardAsync();
        var two = await NewBoardAsync();
        var a = await AddTaskAsync(one);
        var b = await AddTaskAsync(two);
        var service = new TaskService(db.DataSource);

        Assert.Equal(EdgeStatus.CrossProject, (await service.AddBlockerAsync(a, b, _owner, Ct)).Status);
        Assert.Equal(EdgeStatus.NotFound, (await service.AddBlockerAsync(a, Guid.CreateVersion7(), _owner, Ct)).Status);
        Assert.Equal(EdgeStatus.SelfEdge, (await service.AddBlockerAsync(a, a, _owner, Ct)).Status);
    }

    [Fact]
    public async Task Removing_the_only_blocker_unblocks_and_is_recorded()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var blocker = await AddTaskAsync(board, TaskState.Ready);
        var blocked = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(blocked, blocker, _owner, Ct);

        var removed = await service.RemoveBlockerAsync(blocked, blocker, _owner, Ct);
        var again = await service.RemoveBlockerAsync(blocked, blocker, _owner, Ct);

        Assert.Equal(EdgeStatus.Removed, removed.Status);
        Assert.Equal(EdgeStatus.NotPresent, again.Status);
        Assert.Equal(1, await EventCountAsync(blocked, TaskEventKinds.Unblocked));
        Assert.Equal(TransitionStatus.Applied, (await service.TransitionAsync(blocked, TaskState.InProgress, Agent(), null, 1, Ct)).Status);
    }
}
