using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>Withdrawal records, the writer that composes the ports with them, and the Kanban reader, against PostgreSQL.</summary>
public sealed class ImportWithdrawalStoreTests
{
    private sealed class FakeGc : IGcWithdrawalMarker
    {
        public List<string> Calls { get; } = [];

        public bool Fail { get; set; }

        public Task MarkWithdrawnAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("gc refused");
            }
            Calls.Add(jobId + "/" + batchIndex + "/" + string.Join(',', sourceKeys));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVault : IVaultWithdrawalReverter
    {
        public List<string> Calls { get; } = [];

        public Task RevertAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
        {
            Calls.Add(jobId + "/" + batchIndex + "/" + string.Join(',', sourceKeys));
            return Task.CompletedTask;
        }
    }

    private static async Task<PgTestDatabase> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await PgTestDatabase.CreateAsync("withdraw", PostgresImportJobStoreTests.MigrateAsync, ct);
        await new PostgresImportJobStore(db.DataSource).CreateJobAsync(ImportStoreFixtures.NewJob("job-1"), ct);
        return db;
    }

    [Fact]
    public async Task Records_are_idempotent_and_report_what_was_new()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresImportWithdrawalStore(db.DataSource);
        ImportWithdrawalRecord[] batch = [new("a", ImportWithdrawalTargetKind.GcMessage, "a"), new("b", ImportWithdrawalTargetKind.VaultFile, "b")];

        Assert.Equal(2, await store.RecordAsync("job-1", batch, ct));
        Assert.Equal(0, await store.RecordAsync("job-1", batch, ct));
        Assert.Equal(1, await store.RecordAsync("job-1", [.. batch, new("c", ImportWithdrawalTargetKind.GcMessage, "c")], ct));
    }

    [Fact]
    public async Task Keys_are_read_back_by_kind_and_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await new PostgresImportJobStore(db.DataSource).CreateJobAsync(ImportStoreFixtures.NewJob("job-2"), ct);
        var store = new PostgresImportWithdrawalStore(db.DataSource);
        await store.RecordAsync("job-1", [new("a", ImportWithdrawalTargetKind.GcMessage, "a"), new("t", ImportWithdrawalTargetKind.KanbanTask, "t")], ct);
        await store.RecordAsync("job-2", [new("z", ImportWithdrawalTargetKind.GcMessage, "z")], ct);

        Assert.Equal(["a"], await store.ReadWithdrawnKeysAsync("job-1", [ImportWithdrawalTargetKind.GcMessage, ImportWithdrawalTargetKind.VaultFile], ct));
        Assert.Equal(["t"], await store.ReadWithdrawnKeysAsync("job-1", [ImportWithdrawalTargetKind.KanbanTask], ct));
        Assert.Empty(await store.ReadWithdrawnKeysAsync("job-1", [], ct));
    }

    [Fact]
    public async Task A_record_for_an_unknown_job_is_a_conflict_and_keeps_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresImportWithdrawalStore(db.DataSource);

        await Assert.ThrowsAsync<ImportStoreConflictException>(async () =>
            await store.RecordAsync("missing", [new("a", ImportWithdrawalTargetKind.GcMessage, "a")], ct));
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM import_withdrawal");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task The_writer_records_a_batch_only_after_its_side_effect_succeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresImportWithdrawalStore(db.DataSource);
        var gc = new FakeGc { Fail = true };
        var vault = new FakeVault();
        var writer = new PostgresImportWithdrawalWriter(store, gc, vault);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.WithdrawGcMessagesAsync("job-1", 0, ["a", "b"], ct));
        Assert.Empty(await writer.ReadWithdrawnSourceKeysAsync("job-1", ct));

        gc.Fail = false;
        await writer.WithdrawGcMessagesAsync("job-1", 0, ["a", "b"], ct);
        await writer.RevertVaultFilesAsync("job-1", 1, ["v"], ct);

        Assert.Equal(["a", "b", "v"], (await writer.ReadWithdrawnSourceKeysAsync("job-1", ct)).Order());
        Assert.Equal(["job-1/0/a,b"], gc.Calls);
        Assert.Equal(["job-1/1/v"], vault.Calls);
    }

    [Fact]
    public async Task ImportWithdrawer_resumes_from_the_record_after_a_failed_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresImportWithdrawalStore(db.DataSource);
        var gc = new FakeGc();
        var writer = new PostgresImportWithdrawalWriter(store, gc, new FakeVault());
        var target = new FixedTarget(["a", "b", "c"], ["v"]);
        var audit = new CollectingAudit();

        // First run: the second GC batch is refused, so only the first batch is recorded.
        var failing = new FailingSecondBatch(writer);
        var first = await ImportWithdrawer.WithdrawAsync("job-1", target, failing, audit, 2, TimeProvider.System, ct);
        Assert.False(first.Succeeded);
        Assert.Equal(["a", "b"], first.WithdrawnSourceKeys);

        // Second run reads the record, skips a and b, and finishes c and the Vault file.
        var second = await ImportWithdrawer.WithdrawAsync("job-1", target, writer, audit, 2, TimeProvider.System, ct);
        Assert.True(second.Succeeded);
        Assert.Equal(["c", "v"], second.WithdrawnSourceKeys);
        Assert.Equal(2, second.SkippedCount);
    }

    [Fact]
    public async Task The_kanban_reader_returns_the_jobs_tasks_and_the_withdrawn_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var db = await ExportReaderDb.OpenAsync(ct);
        await new PostgresImportJobStore(db.DataSource).CreateJobAsync(ImportStoreFixtures.NewJob("job-1"), ct);
        var gc = await db.CreateGcAsync();
        var t1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var t2 = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var t3 = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var project = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
        foreach (var (id, job) in new (Guid, string?)[] { (t2, "job-1"), (t1, "job-1"), (t3, null) })
        {
            await db.ExecAsync("INSERT INTO tasks (id, project_id, title, state, origin_gc_id, import_job_id) VALUES ($1, $2, 'task', 'backlog', $3, $4)", id, project, gc, job);
        }
        var withdrawals = new PostgresImportWithdrawalStore(db.DataSource);
        var reader = new PostgresKanbanImportedTaskReader(db.DataSource, withdrawals);
        await withdrawals.RecordAsync("job-1", [new(t1.ToString("D"), ImportWithdrawalTargetKind.KanbanTask, t1.ToString("D"))], ct);

        Assert.Equal([t1.ToString("D"), t2.ToString("D")], await reader.ReadImportedTaskIdsAsync("job-1", ct));
        Assert.Empty(await reader.ReadImportedTaskIdsAsync("job-2", ct));
        Assert.Equal([t1.ToString("D")], await reader.ReadWithdrawnTaskIdsAsync("job-1", ct));
    }

    [Fact]
    public async Task An_empty_task_import_job_id_is_refused_by_the_column_check()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();

        await Assert.ThrowsAsync<PostgresException>(async () => await db.ExecAsync(
            "INSERT INTO tasks (project_id, title, state, origin_gc_id, import_job_id) VALUES ($1, 't', 'backlog', $2, ' ')", Guid.NewGuid(), gc));
    }

    private sealed class FixedTarget(string[] gc, string[] vault) : IImportTargetWriter
    {
        public Task<IReadOnlyList<string>> ReadGcSourceKeysAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(gc);

        public Task<IReadOnlyList<string>> ReadVaultSourceKeysAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(vault);

        public Task<IReadOnlySet<string>> ReadCommittedSourceKeysAsync(string jobId, CancellationToken ct) => throw new NotSupportedException();

        public Task AppendGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) => throw new NotSupportedException();

        public Task WriteVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FailingSecondBatch(IImportWithdrawalWriter inner) : IImportWithdrawalWriter
    {
        public Task<IReadOnlySet<string>> ReadWithdrawnSourceKeysAsync(string jobId, CancellationToken ct) => inner.ReadWithdrawnSourceKeysAsync(jobId, ct);

        public Task WithdrawGcMessagesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct) =>
            batchIndex == 1 ? throw new InvalidOperationException("refused") : inner.WithdrawGcMessagesAsync(jobId, batchIndex, sourceKeys, ct);

        public Task RevertVaultFilesAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct) =>
            inner.RevertVaultFilesAsync(jobId, batchIndex, sourceKeys, ct);
    }

    private sealed class CollectingAudit : IAuditSink
    {
        public Task AppendAsync(ImportAuditRecord record, CancellationToken ct) => Task.CompletedTask;
    }
}
