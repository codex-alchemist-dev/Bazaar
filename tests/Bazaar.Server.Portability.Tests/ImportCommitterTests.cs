namespace Bazaar.Server.Portability.Tests;

public class ImportCommitterTests
{
    private const string _hash = "mapping-hash-1";
    private const string _jobId = "job-1";

    private static SourceRecord Vault(string key) =>
        new(key, "document", key, "body", new Dictionary<string, string>());

    private static SourceRecord Gc(string key) =>
        new(key, ImportCommitter.GcEntryKind, key, "entry", new Dictionary<string, string>());

    private static ImportPipelineResult Ready(params SourceRecord[] records) =>
        new(ImportStatus.Ready, "bazaar.import.a", [], new ImportPreview(records, []), _hash);

    [Fact]
    public async Task Refuses_on_hash_mismatch_and_writes_nothing()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();

        var result = await ImportCommitter.CommitAsync(Ready(Vault("a")), "other-hash", writer, audit, _jobId, 10, CancellationToken.None);

        Assert.True(result.Refused);
        Assert.False(result.Succeeded);
        Assert.Equal(0, result.BatchCount);
        Assert.Empty(writer.VaultFiles);
        Assert.Empty(writer.GcMessages);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task Refuses_when_the_dry_run_is_not_ready()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();
        var ambiguous = new ImportPipelineResult(ImportStatus.Ambiguous, null, [], null, string.Empty);
        var failed = new ImportPipelineResult(ImportStatus.Failed, "bazaar.import.a", [], null, string.Empty, "boom");

        var fromAmbiguous = await ImportCommitter.CommitAsync(ambiguous, _hash, writer, audit, _jobId, 10, CancellationToken.None);
        var fromFailed = await ImportCommitter.CommitAsync(failed, _hash, writer, audit, _jobId, 10, CancellationToken.None);

        Assert.True(fromAmbiguous.Refused);
        Assert.True(fromFailed.Refused);
        Assert.Empty(writer.VaultFiles);
        Assert.Empty(writer.GcMessages);
    }

    [Fact]
    public async Task Splits_records_into_batches_and_attaches_provenance()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("a"), Vault("b"), Vault("c"), Vault("d"), Vault("e"));

        var result = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 2, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.BatchCount);
        Assert.Equal(["a", "b", "c", "d", "e"], result.CommittedSourceKeys);
        Assert.Equal(5, writer.VaultFiles.Count);
        Assert.All(writer.VaultFiles, item => Assert.Equal(_jobId, item.Provenance.JobId));
        Assert.All(writer.VaultFiles, item => Assert.Equal(item.Record.SourceKey, item.Provenance.SourceKey));
    }

    [Fact]
    public async Task Batches_never_mix_targets()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("v1"), Vault("v2"), Gc("g1"), Vault("v3"));

        var result = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 10, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.BatchCount);
        Assert.Equal(["v1", "v2", "v3"], writer.VaultFiles.Select(item => item.Record.SourceKey));
        Assert.Equal(["g1"], writer.GcMessages.Select(item => item.Record.SourceKey));
    }

    [Fact]
    public async Task Stops_at_a_failing_batch_and_reports_its_index()
    {
        var writer = new InMemoryImportTargetWriter { FailOnBatchIndex = 1 };
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("a"), Vault("b"), Vault("c"), Vault("d"), Vault("e"));

        var result = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 2, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Refused);
        Assert.NotNull(result.FirstFailure);
        Assert.Equal(1, result.FirstFailure.BatchIndex);
        Assert.Equal(ImportTargetKind.VaultFile, result.FirstFailure.Target);
        Assert.Equal(ImportBatchStage.Write, result.FirstFailure.Stage);
        Assert.Equal(1, result.BatchCount);
        Assert.Equal(["a", "b"], result.CommittedSourceKeys);
        // Batch 0 is written; batch 1 is rejected; batch 2 is never attempted.
        Assert.Equal(["a", "b"], writer.VaultFiles.Select(item => item.Record.SourceKey));
        Assert.Single(audit.Records);
    }

    [Fact]
    public async Task Rerun_after_success_commits_nothing_new()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("a"), Vault("b"), Gc("g1"));

        var first = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 10, CancellationToken.None);
        var second = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 10, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(2, first.BatchCount);
        Assert.True(second.Succeeded);
        Assert.Equal(0, second.BatchCount);
        Assert.Empty(second.CommittedSourceKeys);
        Assert.Equal(3, second.SkippedCount);
        Assert.Equal(2, writer.VaultFiles.Count);
        Assert.Single(writer.GcMessages);
        Assert.Equal(2, audit.Records.Count);
    }

    [Fact]
    public async Task Writes_one_audit_record_per_batch_with_count_and_hash()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("a"), Vault("b"), Vault("c"));

        var result = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 2, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, audit.Records.Count);
        Assert.All(audit.Records, r => Assert.Equal(ImportAuditRecord.BatchKind, r.Kind));
        Assert.All(audit.Records, r => Assert.Equal(_jobId, r.JobId));
        Assert.Equal([1, 2], audit.Records.Select(r => r.BatchNumber));
        Assert.Equal([2, 1], audit.Records.Select(r => r.ItemCount));
        Assert.Equal(ImportBatchHash.Of(writer.VaultFiles.Take(2).ToList()), audit.Records[0].ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", audit.Records[0].ContentHash);
    }

    [Fact]
    public async Task Failed_audit_write_stops_the_job_and_reports_the_audit_stage()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink { FailAppends = true };
        var dryRun = Ready(Vault("a"), Vault("b"), Vault("c"));

        var result = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 2, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FirstFailure);
        Assert.Equal(0, result.FirstFailure.BatchIndex);
        Assert.Equal(ImportBatchStage.Audit, result.FirstFailure.Stage);
        // The first batch was written before its audit failed; the second batch is never attempted.
        Assert.Equal(["a", "b"], writer.VaultFiles.Select(item => item.Record.SourceKey));
        Assert.Equal(["a", "b"], result.CommittedSourceKeys);
    }

    [Fact]
    public async Task Rejects_a_batch_size_below_one()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ImportCommitter.CommitAsync(Ready(Vault("a")), _hash, writer, audit, _jobId, 0, CancellationToken.None));
    }
}
