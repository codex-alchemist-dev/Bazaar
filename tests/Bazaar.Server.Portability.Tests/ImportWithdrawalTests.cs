namespace Bazaar.Server.Portability.Tests;

public class ImportWithdrawalTests
{
    private const string _hash = "h";
    private const string _jobId = "job-1";

    private sealed class FakeWithdrawals : IImportWithdrawalWriter
    {
        private readonly HashSet<string> _done = new(StringComparer.Ordinal);

        public List<(string Target, int Batch, string[] Keys)> Calls { get; } = [];

        public int? FailOnBatchIndex { get; set; }

        public Task<IReadOnlySet<string>> ReadWithdrawnSourceKeysAsync(string jobId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(_done.ToHashSet(StringComparer.Ordinal));

        public Task WithdrawGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct) =>
            Record("gc", batchIndex, sourceKeys);

        public Task RevertVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct) =>
            Record("vault", batchIndex, sourceKeys);

        private Task Record(string target, int batchIndex, IReadOnlyList<string> keys)
        {
            if (batchIndex == FailOnBatchIndex)
            {
                throw new InvalidOperationException("injected");
            }
            Calls.Add((target, batchIndex, [.. keys]));
            foreach (var key in keys)
            {
                _done.Add(key);
            }
            return Task.CompletedTask;
        }
    }

    private static SourceRecord Vault(string key) => new(key, "document", key, "b", new Dictionary<string, string>());

    private static SourceRecord Gc(string key) => new(key, ImportCommitter.GcEntryKind, key, "e", new Dictionary<string, string>());

    private static async Task<InMemoryImportTargetWriter> CommittedAsync()
    {
        var target = new InMemoryImportTargetWriter();
        var dryRun = new ImportPipelineResult(ImportStatus.Ready, "i", [], new ImportPreview([Vault("v2"), Vault("v1"), Gc("g1"), Gc("g2")], []), _hash);
        var result = await ImportCommitter.CommitAsync(dryRun, _hash, target, new InMemoryAuditSink(), _jobId, 10, CancellationToken.None);
        Assert.True(result.Succeeded);
        return target;
    }

    [Fact]
    public async Task Plan_counts_each_target()
    {
        var target = await CommittedAsync();
        var plan = await ImportWithdrawer.PlanAsync(_jobId, target, new FakeWithdrawals(), CancellationToken.None);
        Assert.Equal(2, plan.GcCount);
        Assert.Equal(2, plan.VaultCount);
        Assert.Equal(4, plan.Total);
    }

    [Fact]
    public async Task Withdraws_gc_then_vault_in_sorted_batches_and_audits_each()
    {
        var target = await CommittedAsync();
        var withdrawals = new FakeWithdrawals();
        var audit = new InMemoryAuditSink();

        var result = await ImportWithdrawer.WithdrawAsync(_jobId, target, withdrawals, audit, 1, TimeProvider.System, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.BatchCount);
        Assert.Equal(["g1", "g2", "v1", "v2"], result.WithdrawnSourceKeys);
        Assert.Equal(["gc", "gc", "vault", "vault"], withdrawals.Calls.Select(c => c.Target));
        Assert.Equal(4, audit.Records.Count);
        Assert.All(audit.Records, r => Assert.Equal(ImportWithdrawer.AuditKind, r.Kind));
        Assert.Equal([1, 2, 3, 4], audit.Records.Select(r => r.BatchNumber));
    }

    [Fact]
    public async Task Never_deletes_the_imported_history()
    {
        var target = await CommittedAsync();
        await ImportWithdrawer.WithdrawAsync(_jobId, target, new FakeWithdrawals(), new InMemoryAuditSink(), 10, TimeProvider.System, CancellationToken.None);
        Assert.Equal(2, target.GcMessages.Count);
        Assert.Equal(2, target.VaultFiles.Count);
    }

    [Fact]
    public async Task Rerun_after_success_withdraws_nothing_new()
    {
        var target = await CommittedAsync();
        var withdrawals = new FakeWithdrawals();
        var audit = new InMemoryAuditSink();
        await ImportWithdrawer.WithdrawAsync(_jobId, target, withdrawals, audit, 10, TimeProvider.System, CancellationToken.None);

        var second = await ImportWithdrawer.WithdrawAsync(_jobId, target, withdrawals, audit, 10, TimeProvider.System, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(0, second.BatchCount);
        Assert.Equal(4, second.SkippedCount);
        Assert.Equal(2, audit.Records.Count);
    }

    [Fact]
    public async Task Write_failure_stops_and_a_rerun_resumes()
    {
        var target = await CommittedAsync();
        var withdrawals = new FakeWithdrawals { FailOnBatchIndex = 1 };
        var audit = new InMemoryAuditSink();

        var first = await ImportWithdrawer.WithdrawAsync(_jobId, target, withdrawals, audit, 2, TimeProvider.System, CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(1, first.BatchCount);
        Assert.Equal(ImportBatchStage.Write, first.FirstFailure!.Stage);
        Assert.Equal(ImportTargetKind.VaultFile, first.FirstFailure.Target);

        withdrawals.FailOnBatchIndex = null;
        var second = await ImportWithdrawer.WithdrawAsync(_jobId, target, withdrawals, audit, 2, TimeProvider.System, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal(["v1", "v2"], second.WithdrawnSourceKeys);
    }

    [Fact]
    public async Task Audit_failure_stops_and_reports_the_audit_stage()
    {
        var target = await CommittedAsync();
        var audit = new InMemoryAuditSink { FailAppends = true };

        var result = await ImportWithdrawer.WithdrawAsync(_jobId, target, new FakeWithdrawals(), audit, 10, TimeProvider.System, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ImportBatchStage.Audit, result.FirstFailure!.Stage);
        Assert.Equal(1, result.BatchCount);
    }

    [Fact]
    public async Task Rejects_a_batch_size_below_one()
    {
        var target = await CommittedAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ImportWithdrawer.WithdrawAsync(_jobId, target, new FakeWithdrawals(), new InMemoryAuditSink(), 0, TimeProvider.System, CancellationToken.None));
    }
}
