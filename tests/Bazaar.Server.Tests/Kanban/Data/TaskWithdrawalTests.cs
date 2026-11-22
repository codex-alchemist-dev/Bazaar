using System.Text.Json;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The stored <c>withdrawn</c> state through <see cref="IKanbanTaskWithdrawal"/> on real PostgreSQL (doc 12, "Withdrawn state").</summary>
public sealed class TaskWithdrawalTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private const string _reason = "withdrawn by import";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<(Guid ProjectId, Guid GcId)> NewBoardAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        return (Guid.CreateVersion7(), gcId);
    }

    private async Task<Guid> AddTaskAsync((Guid ProjectId, Guid GcId) board, TaskState state, string title = "Task")
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

    [Theory]
    [InlineData(TaskState.Backlog)]
    [InlineData(TaskState.Ready)]
    [InlineData(TaskState.InProgress)]
    [InlineData(TaskState.Review)]
    public async Task The_system_withdraws_a_task_and_its_history_stays(TaskState from)
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, from);
        var service = new TaskService(db.DataSource);
        var before = await ReadAsync(task);

        var status = await service.WithdrawAsync(task, TaskActor.SystemActor, _reason, Ct);

        Assert.Equal(TaskWithdrawalStatus.Withdrawn, status);
        var row = await ReadAsync(task);
        Assert.Equal(TaskState.Withdrawn, row.State);
        Assert.Equal(before.Title, row.Title);
        Assert.Equal(before.Version + 1, row.Version);
        var transition = Assert.Single(
            await new TaskEventRepository(db.DataSource).ListAsync(task, ct: Ct),
            e => e.Kind == TaskEventKinds.Transitioned);
        using var payload = JsonDocument.Parse(transition.PayloadJson);
        Assert.Equal("withdrawn", payload.RootElement.GetProperty("to").GetString());
        Assert.Equal(_reason, payload.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_agent_cannot_withdraw_and_a_reason_is_required()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.InProgress);
        var service = new TaskService(db.DataSource);

        Assert.Equal(TaskWithdrawalStatus.NotAuthorised,
            await service.WithdrawAsync(task, new TaskActor(TaskActorKind.Agent, Guid.CreateVersion7()), _reason, Ct));
        Assert.Equal(TaskWithdrawalStatus.MissingReason, await service.WithdrawAsync(task, _owner, "  ", Ct));
        Assert.Equal(TaskState.InProgress, (await ReadAsync(task)).State);
        Assert.Equal(0, await EventCountAsync(task, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task A_done_task_is_withdrawn_only_by_the_owner()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Done);
        var service = new TaskService(db.DataSource);

        Assert.Equal(TaskWithdrawalStatus.NotAuthorised, await service.WithdrawAsync(task, TaskActor.SystemActor, _reason, Ct));
        Assert.Equal(TaskState.Done, (await ReadAsync(task)).State);

        Assert.Equal(TaskWithdrawalStatus.Withdrawn, await service.WithdrawAsync(task, _owner, _reason, Ct));
        Assert.Equal(TaskState.Withdrawn, (await ReadAsync(task)).State);
    }

    [Fact]
    public async Task Withdrawing_twice_or_an_unknown_task_changes_nothing()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);
        await service.WithdrawAsync(task, TaskActor.SystemActor, _reason, Ct);
        var version = (await ReadAsync(task)).Version;

        Assert.Equal(TaskWithdrawalStatus.AlreadyWithdrawn, await service.WithdrawAsync(task, TaskActor.SystemActor, _reason, Ct));
        Assert.Equal(TaskWithdrawalStatus.UnknownTask, await service.WithdrawAsync(Guid.CreateVersion7(), TaskActor.SystemActor, _reason, Ct));

        Assert.Equal(version, (await ReadAsync(task)).Version);
        Assert.Equal(1, await EventCountAsync(task, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task A_normal_transition_cannot_leave_withdrawn()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);
        await service.WithdrawAsync(task, TaskActor.SystemActor, _reason, Ct);
        var version = (await ReadAsync(task)).Version;

        var result = await service.TransitionAsync(task, TaskState.Backlog, _owner, "undo", version, Ct);

        Assert.Equal(TransitionStatus.Invalid, result.Status);
        Assert.Equal(TaskState.Withdrawn, (await ReadAsync(task)).State);
    }

    [Fact]
    public async Task A_normal_transition_cannot_enter_withdrawn()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Ready);
        var service = new TaskService(db.DataSource);

        var result = await service.TransitionAsync(task, TaskState.Withdrawn, _owner, _reason, 1, Ct);

        Assert.Equal(TransitionStatus.Invalid, result.Status);
        Assert.Equal(TaskState.Ready, (await ReadAsync(task)).State);
    }

    [Fact]
    public async Task A_withdrawn_blocker_stops_blocking_its_dependents()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var blocker = await AddTaskAsync(board, TaskState.InProgress, "blocker");
        var dependent = await AddTaskAsync(board, TaskState.Ready, "dependent");
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(dependent, blocker, _owner, Ct);
        Assert.False((await service.CheckStartAsync(dependent, Ct)).Allowed);

        await service.WithdrawAsync(blocker, TaskActor.SystemActor, _reason, Ct);

        Assert.True((await service.CheckStartAsync(dependent, Ct)).Allowed);
        Assert.Equal(1, await EventCountAsync(dependent, TaskEventKinds.Unblocked));
        // The dependent is unblocked, not moved: a withdrawal never auto-readies.
        Assert.Equal(TaskState.Ready, (await ReadAsync(dependent)).State);
        var start = await service.TransitionAsync(dependent, TaskState.InProgress, new TaskActor(TaskActorKind.Agent, Guid.CreateVersion7()), null, 1, Ct);
        Assert.Equal(TransitionStatus.Applied, start.Status);
    }

    [Fact]
    public async Task The_board_hides_withdrawn_cards_by_default_and_shows_them_on_request()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var kept = await AddTaskAsync(board, TaskState.Ready, "kept");
        var gone = await AddTaskAsync(board, TaskState.Backlog, "gone");
        var blocked = await AddTaskAsync(board, TaskState.Backlog, "blocked by gone");
        var service = new TaskService(db.DataSource);
        await service.AddBlockerAsync(blocked, gone, _owner, Ct);
        await service.WithdrawAsync(gone, TaskActor.SystemActor, _reason, Ct);

        var snapshot = await service.GetBoardSnapshotAsync(board.ProjectId, Ct);

        Assert.DoesNotContain(snapshot.Cards, c => c.Id == gone);
        Assert.Contains(snapshot.Cards, c => c.Id == kept);
        Assert.Empty(Assert.Single(snapshot.Cards, c => c.Id == blocked).UnfinishedBlockerIds);

        var all = await service.GetBoardSnapshotAsync(board.ProjectId, includeWithdrawn: true, Ct);
        Assert.Equal("withdrawn", Assert.Single(all.Cards, c => c.Id == gone).State);
    }
}
