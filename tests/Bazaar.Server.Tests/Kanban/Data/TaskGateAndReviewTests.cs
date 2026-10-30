using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>Gate decisions, the review-approval flag and the merged-PR hook on real PostgreSQL (doc 12, WP9 and WP12).</summary>
public sealed class TaskGateAndReviewTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private sealed record Board(Guid ProjectId, Guid GcId);

    private async Task<Board> NewBoardAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        return new Board(Guid.CreateVersion7(), gcId);
    }

    private async Task<Guid> AddTaskAsync(Board board, TaskState state, TaskGate gate = TaskGate.None, Guid? owner = null)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, board.ProjectId, "Task", state, owner, gate, board.GcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private async Task LinkAsync(Guid taskId, TaskLinkKind kind, string reference)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskLinkRepository(db.DataSource).InsertAsync(tx, new TaskLink(taskId, kind, reference), Ct);
        await tx.CommitAsync(Ct);
    }

    private async Task<TaskRecord> ReadAsync(Guid id) => (await new TaskRepository(db.DataSource).GetAsync(id, Ct))!;

    private async Task<long> EventCountAsync(Guid taskId, string kind)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM task_events WHERE task_id = $1 AND kind = $2");
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(kind);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task An_owner_gate_blocks_review_to_done_until_the_owner_approves()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Review, TaskGate.Owner);
        var service = new TaskService(db.DataSource);

        var refused = await service.TransitionAsync(task, TaskState.Done, _owner, null, 1, Ct);
        Assert.Equal(TransitionOutcome.GateNotSatisfied, refused.Outcome);

        var recorded = await service.RecordGateDecisionAsync(task, TaskGateOutcome.Approved, _owner, "owner:approve", Ct);
        Assert.Equal(TransitionStatus.Applied, recorded.Status);
        Assert.Equal(1, await EventCountAsync(task, TaskEventKinds.GateRecorded));

        var done = await service.TransitionAsync(task, TaskState.Done, _owner, null, 1, Ct);
        Assert.Equal(TransitionStatus.Applied, done.Status);
        Assert.Equal(TaskState.Done, (await ReadAsync(task)).State);
    }

    [Fact]
    public async Task The_latest_gate_decision_wins_so_a_rejection_after_an_approval_blocks_done()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Review, TaskGate.Owner);
        var service = new TaskService(db.DataSource);

        await service.RecordGateDecisionAsync(task, TaskGateOutcome.Approved, _owner, "owner:approve", Ct);
        await service.RecordGateDecisionAsync(task, TaskGateOutcome.Rejected, _owner, "owner:reject", Ct);

        var result = await service.TransitionAsync(task, TaskState.Done, _owner, null, 1, Ct);
        Assert.Equal(TransitionOutcome.GateNotSatisfied, result.Outcome);
    }

    [Fact]
    public async Task A_council_gate_is_decided_only_by_the_system_actor()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Review, TaskGate.Council);
        var service = new TaskService(db.DataSource);

        var byOwner = await service.RecordGateDecisionAsync(task, TaskGateOutcome.Approved, _owner, "council:1", Ct);
        Assert.Equal(TransitionOutcome.NotAuthorised, byOwner.Outcome);
        Assert.Equal(0, await EventCountAsync(task, TaskEventKinds.GateRecorded));

        var bySystem = await service.RecordGateDecisionAsync(task, TaskGateOutcome.Approved, TaskActor.SystemActor, "council:1", Ct);
        Assert.Equal(TransitionStatus.Applied, bySystem.Status);

        var done = await service.TransitionAsync(task, TaskState.Done, _owner, null, 1, Ct);
        Assert.Equal(TransitionStatus.Applied, done.Status);
    }

    [Fact]
    public async Task Recording_a_decision_on_an_ungated_task_is_invalid()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Review);
        var service = new TaskService(db.DataSource);

        var result = await service.RecordGateDecisionAsync(task, TaskGateOutcome.Approved, _owner, "owner:approve", Ct);

        Assert.Equal(TransitionStatus.Invalid, result.Status);
        Assert.Equal(0, await EventCountAsync(task, TaskEventKinds.GateRecorded));
    }

    [Fact]
    public async Task A_review_approval_sets_the_flag_and_moves_nothing()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var assignee = Guid.CreateVersion7();
        var task = await AddTaskAsync(board, TaskState.Review, owner: assignee);
        var service = new TaskService(db.DataSource);
        var reviewer = new TaskActor(TaskActorKind.Agent, Guid.CreateVersion7());

        var result = await service.RecordReviewApprovalAsync(task, reviewer, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        var row = await ReadAsync(task);
        Assert.Equal(TaskState.Review, row.State);
        Assert.Equal(reviewer.Id, row.ReviewApprovedBy);
        Assert.NotNull(row.ReviewApprovedAt);
        Assert.Equal(1, await EventCountAsync(task, TaskEventKinds.ReviewApproved));
    }

    [Fact]
    public async Task The_assignee_cannot_approve_their_own_review()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var assignee = Guid.CreateVersion7();
        var task = await AddTaskAsync(board, TaskState.Review, owner: assignee);
        var service = new TaskService(db.DataSource);

        var result = await service.RecordReviewApprovalAsync(task, new TaskActor(TaskActorKind.Agent, assignee), Ct);

        Assert.Equal(TransitionStatus.Refused, result.Status);
        Assert.Equal(TransitionOutcome.NotAuthorised, result.Outcome);
        Assert.Null((await ReadAsync(task)).ReviewApprovedBy);
    }

    [Fact]
    public async Task A_review_approval_on_a_card_not_in_review_is_invalid()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.InProgress);
        var service = new TaskService(db.DataSource);

        var result = await service.RecordReviewApprovalAsync(task, _owner, Ct);

        Assert.Equal(TransitionStatus.Invalid, result.Status);
        Assert.Null((await ReadAsync(task)).ReviewApprovedBy);
    }

    [Fact]
    public async Task A_merged_pr_moves_its_review_card_to_done_with_the_system_actor()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Review);
        const string pr = "codex-alchemist-dev/Bazaar#900";
        await LinkAsync(task, TaskLinkKind.Pr, pr);
        var service = new TaskService(db.DataSource);

        var outcomes = await service.PrMergedAsync(pr, Ct);

        Assert.Single(outcomes);
        Assert.Equal(TransitionStatus.Applied, outcomes[0].Result.Status);
        Assert.Equal(TaskState.Done, (await ReadAsync(task)).State);
        Assert.Equal(1, await EventCountAsync(task, TaskEventKinds.Transitioned));
    }

    [Fact]
    public async Task A_pr_card_cannot_be_moved_to_done_by_hand_even_after_review_approval()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Review);
        await LinkAsync(task, TaskLinkKind.Pr, "codex-alchemist-dev/Bazaar#901");
        var service = new TaskService(db.DataSource);
        await service.RecordReviewApprovalAsync(task, _owner, Ct);

        var result = await service.TransitionAsync(task, TaskState.Done, _owner, null, 2, Ct);

        Assert.Equal(TransitionOutcome.PrNotMerged, result.Outcome);
        Assert.Equal(TaskState.Review, (await ReadAsync(task)).State);
    }

    [Fact]
    public async Task A_merged_pr_leaves_cards_that_are_not_in_review_alone()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board, TaskState.Ready);
        const string pr = "codex-alchemist-dev/Bazaar#902";
        await LinkAsync(task, TaskLinkKind.Pr, pr);
        var service = new TaskService(db.DataSource);

        var outcomes = await service.PrMergedAsync(pr, Ct);

        Assert.Single(outcomes);
        Assert.NotEqual(TransitionStatus.Applied, outcomes[0].Result.Status);
        Assert.Equal(TaskState.Ready, (await ReadAsync(task)).State);
    }
}
