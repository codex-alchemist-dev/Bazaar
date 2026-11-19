using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Anchors;
using Bazaar.Server.Security.Audit.Checkpoints;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Tests.Security.Audit.Db;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.Audit.Anchors.Db;

/// <summary>PostgresAuditAnchorStore against a real PostgreSQL: receipts match a stored checkpoint, one receipt per target (design doc 48, 5.3).</summary>
public sealed class PostgresAuditAnchorStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    /// <summary>Writes five audit rows, stores a checkpoint over them, and returns that checkpoint.</summary>
    private static async Task<AuditCheckpoint> StoreCheckpointAsync(AuditTestDb db)
    {
        var log = new PostgresAuditLog(db.DataSource);
        for (var i = 0; i < 5; i++)
            await log.AppendAsync(new AuditEntry(_t0.AddSeconds(i), "agent:charles", "tool.call", $"{{\"n\": {i}}}"), Ct);

        var records = new List<AuditRecord>();
        await foreach (var record in log.StreamAsync(Ct))
            records.Add(record);
        var head = records.Single(r => r.Seq == 5);

        var checkpoint = new AuditCheckpoint(
            FirstSeq: 1,
            LastSeq: 5,
            RowCount: 5,
            ChainHeadHash: head.Hash,
            MerkleRoot: Enumerable.Repeat((byte)0x22, 32).ToArray(),
            CreatedAt: _t0.AddMinutes(5));
        await new PostgresAuditCheckpointStore(db.DataSource).AppendAsync(
            new StoredAuditCheckpoint(checkpoint, "audit-key-1", Enumerable.Repeat((byte)0x5A, 64).ToArray()), Ct);
        return checkpoint;
    }

    private static AuditAnchorReceipt Receipt(string targetId, byte fill) =>
        new(targetId, Enumerable.Repeat(fill, 40).ToArray(), _t0.AddMinutes(6));

    [Fact]
    public async Task Receipts_read_back_for_their_checkpoint_in_target_order()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var checkpoint = await StoreCheckpointAsync(db);
        var store = new PostgresAuditAnchorStore(db.DataSource);

        await store.AppendAsync(checkpoint, Receipt("rfc3161", 0x31), Ct);
        await store.AppendAsync(checkpoint, Receipt("offsite-git", 0x32), Ct);

        var read = await store.ReadForCheckpointAsync(checkpoint, Ct);
        Assert.Equal(["offsite-git", "rfc3161"], read.Select(r => r.TargetId));
        Assert.Equal(Receipt("offsite-git", 0x32).ReceiptBytes, read[0].ReceiptBytes);
        Assert.Equal(_t0.AddMinutes(6), read[0].AnchoredAt);
    }

    [Fact]
    public async Task A_second_receipt_from_the_same_target_is_refused()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var checkpoint = await StoreCheckpointAsync(db);
        var store = new PostgresAuditAnchorStore(db.DataSource);
        await store.AppendAsync(checkpoint, Receipt("rfc3161", 0x31), Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => store.AppendAsync(checkpoint, Receipt("rfc3161", 0x33), Ct));
    }

    [Fact]
    public async Task A_receipt_for_a_checkpoint_that_is_not_stored_is_refused()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var checkpoint = await StoreCheckpointAsync(db);
        var unknown = checkpoint with { MerkleRoot = Enumerable.Repeat((byte)0x99, 32).ToArray() };
        var store = new PostgresAuditAnchorStore(db.DataSource);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(unknown, Receipt("rfc3161", 0x31), Ct));
        Assert.Empty(await store.ReadForCheckpointAsync(unknown, Ct));
    }

    [Fact]
    public async Task A_receipt_and_an_audit_row_written_in_one_transaction_roll_back_together()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var checkpoint = await StoreCheckpointAsync(db);
        var store = new PostgresAuditAnchorStore(db.DataSource);
        var log = new PostgresAuditLog(db.DataSource);
        var entry = new AuditEntry(_t0.AddMinutes(7), "system", AuditAnchorService.RecoveredKind, "{\"last_seq\": 5, \"target_id\": \"rfc3161\"}");

        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await PostgresAuditAnchorStore.AppendInTransactionAsync(conn, tx, checkpoint, Receipt("rfc3161", 0x31), Ct);
            await log.AppendInTransactionAsync(conn, tx, entry, Ct);
            await tx.RollbackAsync(Ct);
        }

        Assert.Empty(await store.ReadForCheckpointAsync(checkpoint, Ct));
        Assert.False(await new PostgresAuditAnchorFailureLog(db.DataSource).HasRecordedFailureAsync(5, "rfc3161", Ct));
        Assert.Equal(5, await CountAuditRowsAsync(db));
    }

    [Fact]
    public async Task The_atomic_writer_commits_the_receipt_and_the_recovered_event_together()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var checkpoint = await StoreCheckpointAsync(db);
        var store = new PostgresAuditAnchorStore(db.DataSource);
        var log = new PostgresAuditLog(db.DataSource);
        await log.AppendAsync(new AuditEntry(_t0.AddMinutes(6), "system", AuditAnchorService.FailedKind, "{\"last_seq\": 5, \"target_id\": \"rfc3161\", \"reason\": \"timeout\"}"), Ct);
        var writer = new PostgresAuditAnchorAtomicWriter(db.DataSource, log);

        await using (var scope = await writer.BeginAsync(Ct))
        {
            await scope.AppendReceiptAsync(checkpoint, Receipt("rfc3161", 0x31), Ct);
            Assert.True(await scope.HasRecordedFailureAsync(5, "rfc3161", Ct));
            await scope.AppendAuditAsync(new AuditEntry(_t0.AddMinutes(7), "system", AuditAnchorService.RecoveredKind, "{\"last_seq\": 5, \"target_id\": \"rfc3161\"}"), Ct);
            await scope.CommitAsync(Ct);
        }

        Assert.Single(await store.ReadForCheckpointAsync(checkpoint, Ct));
        Assert.Equal(7, await CountAuditRowsAsync(db));
    }

    [Fact]
    public async Task A_scope_disposed_without_commit_leaves_no_receipt()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var checkpoint = await StoreCheckpointAsync(db);
        var store = new PostgresAuditAnchorStore(db.DataSource);
        var writer = new PostgresAuditAnchorAtomicWriter(db.DataSource, new PostgresAuditLog(db.DataSource));

        await using (var scope = await writer.BeginAsync(Ct))
        {
            await scope.AppendReceiptAsync(checkpoint, Receipt("rfc3161", 0x31), Ct);
        }

        Assert.Empty(await store.ReadForCheckpointAsync(checkpoint, Ct));
    }

    private static async Task<long> CountAuditRowsAsync(AuditTestDb db)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM audit_log");
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
