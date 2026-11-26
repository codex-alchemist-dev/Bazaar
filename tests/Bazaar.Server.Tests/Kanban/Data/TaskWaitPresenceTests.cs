using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>A card an agent holds that becomes blocked, or is freed, reaches the wait sink after commit (doc 12, doc 23).</summary>
public sealed class TaskWaitPresenceTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private sealed class FakeSink(bool fail = false) : IKanbanWaitSink
    {
        public List<(Guid Agent, DateTimeOffset At, bool Waiting)> Calls { get; } = [];

        public ValueTask OnWaitChangedAsync(Guid agentId, DateTimeOffset at, bool waiting, CancellationToken ct)
        {
            Calls.Add((agentId, at, waiting));
            return fail ? throw new InvalidOperationException("sink down") : ValueTask.CompletedTask;
        }
    }

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<(Guid ProjectId, Guid GcId)> NewBoardAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        return (Guid.CreateVersion7(), gcId);
    }

    private async Task<Guid> AddTaskAsync((Guid ProjectId, Guid GcId) board, TaskState state, Guid? ownerAgent = null)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, board.ProjectId, "Task", state, ownerAgent, TaskGate.None, board.GcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    [Fact]
    public async Task Adding_and_removing_a_blocker_reports_the_holding_agent_waiting_then_free()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var agent = Guid.CreateVersion7();
        var blocker = await AddTaskAsync(board, TaskState.Ready);
        var held = await AddTaskAsync(board, TaskState.Ready, agent);
        var sink = new FakeSink();
        var service = new TaskService(db.DataSource, waitSink: sink);

        await service.AddBlockerAsync(held, blocker, _owner, Ct);
        await service.RemoveBlockerAsync(held, blocker, _owner, Ct);

        Assert.Equal([(agent, true), (agent, false)], sink.Calls.Select(c => (c.Agent, c.Waiting)));
    }

    [Fact]
    public async Task A_card_nobody_holds_reports_nothing()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var blocker = await AddTaskAsync(board, TaskState.Ready);
        var unheld = await AddTaskAsync(board, TaskState.Ready);
        var sink = new FakeSink();
        var service = new TaskService(db.DataSource, waitSink: sink);

        await service.AddBlockerAsync(unheld, blocker, _owner, Ct);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public async Task A_failing_sink_does_not_fail_the_board_change()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var blocker = await AddTaskAsync(board, TaskState.Ready);
        var held = await AddTaskAsync(board, TaskState.Ready, Guid.CreateVersion7());
        var failures = new List<Exception>();
        var service = new TaskService(db.DataSource, waitSink: new FakeSink(fail: true), onWaitSinkFailure: failures.Add);

        var result = await service.AddBlockerAsync(held, blocker, _owner, Ct);

        Assert.Equal(EdgeStatus.Added, result.Status);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }
}
