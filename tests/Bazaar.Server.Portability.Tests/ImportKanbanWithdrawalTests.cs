namespace Bazaar.Server.Portability.Tests;

public class ImportKanbanWithdrawalTests
{
    private sealed class FakeTasks(params string[] ids) : IKanbanWithdrawalWriter
    {
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);

        public List<(string[] Ids, string Reason)> Calls { get; } = [];

        public bool Fail { get; set; }

        public Task<IReadOnlyList<string>> ReadImportedTaskIdsAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(ids);

        public Task<IReadOnlySet<string>> ReadWithdrawnTaskIdsAsync(string jobId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(_done.ToHashSet(StringComparer.Ordinal));

        public Task WithdrawTasksAsync(string jobId, int batchIndex, IReadOnlyList<string> taskIds, string reason, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("gate refused");
            }
            Calls.Add(([.. taskIds], reason));
            foreach (var id in taskIds)
            {
                _done.Add(id);
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Blocks_tasks_with_the_doc_reason_in_audited_batches()
    {
        var tasks = new FakeTasks("t2", "t1", "t3");
        var audit = new InMemoryAuditSink();

        var result = await ImportKanbanWithdrawer.WithdrawAsync("j", tasks, audit, 2, TimeProvider.System, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["t1", "t2", "t3"], result.WithdrawnSourceKeys);
        Assert.Equal(2, audit.Records.Count);
        Assert.All(tasks.Calls, c => Assert.Equal("withdrawn by import", c.Reason));
    }

    [Fact]
    public async Task Rerun_is_a_no_op()
    {
        var tasks = new FakeTasks("t1");
        var audit = new InMemoryAuditSink();
        await ImportKanbanWithdrawer.WithdrawAsync("j", tasks, audit, 5, TimeProvider.System, CancellationToken.None);
        var second = await ImportKanbanWithdrawer.WithdrawAsync("j", tasks, audit, 5, TimeProvider.System, CancellationToken.None);
        Assert.Equal(0, second.BatchCount);
        Assert.Equal(1, second.SkippedCount);
    }

    [Fact]
    public async Task A_refusing_gate_stops_the_job()
    {
        var result = await ImportKanbanWithdrawer.WithdrawAsync("j", new FakeTasks("t1") { Fail = true }, new InMemoryAuditSink(), 5, TimeProvider.System, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(ImportBatchStage.Write, result.FirstFailure!.Stage);
    }
}
