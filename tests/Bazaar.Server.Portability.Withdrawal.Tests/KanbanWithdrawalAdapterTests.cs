using Bazaar.RyuseiFuda.Kanban;

namespace Bazaar.Server.Portability.Withdrawal.Tests;

public class KanbanWithdrawalAdapterTests
{
    private sealed class FakeReader(IReadOnlyList<string> imported) : IImportedKanbanTaskReader
    {
        public HashSet<string> Withdrawn { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<string>> ReadImportedTaskIdsAsync(string jobId, CancellationToken ct) => Task.FromResult(imported);

        public Task<IReadOnlySet<string>> ReadWithdrawnTaskIdsAsync(string jobId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(Withdrawn.ToHashSet(StringComparer.Ordinal));
    }

    private sealed class FakeTasks : IKanbanTaskWithdrawal
    {
        public Dictionary<Guid, TaskWithdrawalStatus> Forced { get; } = [];

        public HashSet<Guid> State { get; } = [];

        public List<(Guid Id, TaskActor Actor, string Reason)> Calls { get; } = [];

        public Task<TaskWithdrawalStatus> WithdrawAsync(Guid taskId, TaskActor actor, string reason, CancellationToken ct = default)
        {
            Calls.Add((taskId, actor, reason));
            if (Forced.TryGetValue(taskId, out var forced))
            {
                return Task.FromResult(forced);
            }
            return Task.FromResult(State.Add(taskId) ? TaskWithdrawalStatus.Withdrawn : TaskWithdrawalStatus.AlreadyWithdrawn);
        }
    }

    private static readonly Guid _a = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid _b = Guid.Parse("00000000-0000-7000-8000-000000000002");
    private static readonly Guid _c = Guid.Parse("00000000-0000-7000-8000-000000000003");

    private static string Id(Guid id) => id.ToString("D");

    [Fact]
    public async Task Withdraws_with_the_system_actor_and_the_import_reason()
    {
        var tasks = new FakeTasks();
        var adapter = new KanbanWithdrawalAdapter(new FakeReader([Id(_a), Id(_b)]), tasks);

        await adapter.WithdrawTasksAsync("job", 0, [Id(_a), Id(_b)], ImportKanbanWithdrawer.Reason, CancellationToken.None);

        Assert.Equal(2, tasks.Calls.Count);
        Assert.All(tasks.Calls, c => Assert.Equal(TaskActor.SystemActor, c.Actor));
        Assert.All(tasks.Calls, c => Assert.Equal("withdrawn by import", c.Reason));
    }

    [Fact]
    public async Task Already_withdrawn_is_a_no_op_so_a_repeat_succeeds()
    {
        var tasks = new FakeTasks();
        var adapter = new KanbanWithdrawalAdapter(new FakeReader([Id(_a)]), tasks);

        await adapter.WithdrawTasksAsync("job", 0, [Id(_a)], ImportKanbanWithdrawer.Reason, CancellationToken.None);
        await adapter.WithdrawTasksAsync("job", 0, [Id(_a)], ImportKanbanWithdrawer.Reason, CancellationToken.None);

        Assert.Single(tasks.State);
    }

    [Theory]
    [InlineData(TaskWithdrawalStatus.NotAuthorised)]
    [InlineData(TaskWithdrawalStatus.UnknownTask)]
    [InlineData(TaskWithdrawalStatus.MissingReason)]
    public async Task A_refusal_fails_the_batch(TaskWithdrawalStatus status)
    {
        var tasks = new FakeTasks();
        tasks.Forced[_b] = status;
        var adapter = new KanbanWithdrawalAdapter(new FakeReader([]), tasks);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.WithdrawTasksAsync("job", 3, [Id(_a), Id(_b), Id(_c)], ImportKanbanWithdrawer.Reason, CancellationToken.None));

        Assert.Contains(status.ToString(), ex.Message);
        Assert.DoesNotContain(_c, tasks.State); // stops at the first refusal
    }

    [Fact]
    public async Task A_task_id_that_is_not_a_guid_fails_the_batch()
    {
        var adapter = new KanbanWithdrawalAdapter(new FakeReader([]), new FakeTasks());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.WithdrawTasksAsync("job", 0, ["not-a-guid"], ImportKanbanWithdrawer.Reason, CancellationToken.None));
    }

    [Fact]
    public async Task Resumes_after_a_partial_failure_through_the_withdrawer()
    {
        var tasks = new FakeTasks();
        tasks.Forced[_b] = TaskWithdrawalStatus.NotAuthorised;
        var reader = new FakeReader([Id(_a), Id(_b), Id(_c)]);
        var adapter = new KanbanWithdrawalAdapter(reader, tasks);
        var audit = new RecordingAuditSink();

        // Batch size 3: _a is withdrawn, _b refuses, so the batch fails and nothing is recorded.
        var first = await ImportKanbanWithdrawer.WithdrawAsync("job", adapter, audit, 3, TimeProvider.System, CancellationToken.None);
        Assert.False(first.Succeeded);
        Assert.Contains(_a, tasks.State);

        tasks.Forced.Clear();
        var second = await ImportKanbanWithdrawer.WithdrawAsync("job", adapter, audit, 3, TimeProvider.System, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal([_a, _b, _c], tasks.State.Order());
    }
}
