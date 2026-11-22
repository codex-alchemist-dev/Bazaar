namespace Bazaar.Server.Portability.Tests;

public class ImportKanbanTargetTests
{
    private const string _hash = "mapping-hash-kanban";
    private const string _jobId = "job-kanban";

    private static SourceRecord KanbanCard(string key) =>
        new(key, ImportCommitter.KanbanTaskKind, key, "card body", new Dictionary<string, string> { ["kanban_column"] = "Ready" });

    private static ImportPipelineResult Ready(params SourceRecord[] records) =>
        new(ImportStatus.Ready, "bazaar.import.trello", [], new ImportPreview(records, []), _hash);

    [Fact]
    public void Kanban_task_records_route_to_the_kanban_target()
    {
        Assert.Equal(ImportTargetKind.KanbanTask, ImportCommitter.TargetFor(KanbanCard("t1")));
    }

    [Fact]
    public async Task Commit_writes_kanban_records_to_the_kanban_target_only()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();

        var result = await ImportCommitter.CommitAsync(Ready(KanbanCard("t1"), KanbanCard("t2")), _hash, writer, audit, _jobId, 10, CancellationToken.None);

        Assert.False(result.Refused);
        Assert.Equal(["t1", "t2"], writer.KanbanTasks.Select(i => i.Record.SourceKey));
        Assert.Empty(writer.VaultFiles);
        Assert.Empty(writer.GcMessages);
        Assert.All(writer.KanbanTasks, item => Assert.Equal(_jobId, item.Provenance.JobId));
    }

    [Fact]
    public async Task Verify_passes_when_every_committed_task_is_readable()
    {
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(KanbanCard("t1"), KanbanCard("t2"));
        var commit = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 10, CancellationToken.None);

        var problems = await ImportVerifier.VerifyAsync(writer, dryRun, commit, _jobId, CancellationToken.None);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task Verify_reports_a_kanban_task_that_was_dropped()
    {
        var inner = new InMemoryImportTargetWriter();
        var writer = new LossyImportTargetWriter(inner, dropKey: "t2");
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(KanbanCard("t1"), KanbanCard("t2"));
        var commit = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 10, CancellationToken.None);

        var problems = await ImportVerifier.VerifyAsync(writer, dryRun, commit, _jobId, CancellationToken.None);

        Assert.Contains(problems, p => p.Contains("kanban", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_writer_without_a_kanban_target_fails_the_job_instead_of_dropping_tasks()
    {
        var writer = new VaultOnlyWriter();
        var audit = new InMemoryAuditSink();

        var result = await ImportCommitter.CommitAsync(Ready(KanbanCard("t1")), _hash, writer, audit, _jobId, 10, CancellationToken.None);

        Assert.NotNull(result.FirstFailure);
        Assert.Equal(ImportTargetKind.KanbanTask, result.FirstFailure!.Target);
        Assert.Empty(result.CommittedSourceKeys);
    }

    /// <summary>A writer that predates the Kanban target: only the three original methods, so the defaults apply.</summary>
    private sealed class VaultOnlyWriter : IImportTargetWriter
    {
        public Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

        public Task AppendGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) => Task.CompletedTask;

        public Task WriteVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ReadGcSourceKeysAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ReadVaultSourceKeysAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
