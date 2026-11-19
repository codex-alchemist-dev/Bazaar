using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Checkpoints;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.Audit.Db;

/// <summary>PostgresAuditCheckpointStore against a real PostgreSQL: continuity, chain-head binding and shape checks (doc 48, 5.2).</summary>
public sealed class AuditCheckpointStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    /// <summary>Appends <paramref name="count"/> audit rows and returns the log.</summary>
    private static async Task<PostgresAuditLog> SeedAuditAsync(AuditTestDb db, int count, int offset = 0)
    {
        var log = new PostgresAuditLog(db.DataSource);
        for (var i = 0; i < count; i++)
            await log.AppendAsync(new AuditEntry(_t0.AddSeconds(offset + i), "agent:charles", "tool.call", $"{{\"n\": {offset + i}}}"), Ct);
        return log;
    }

    /// <summary>A checkpoint over [firstSeq, lastSeq] whose head is the audit row at lastSeq.</summary>
    private static async Task<StoredAuditCheckpoint> CheckpointOverAsync(PostgresAuditLog log, long firstSeq, long lastSeq, byte merkleFill = 0x11)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in log.StreamAsync(Ct))
            records.Add(record);
        var head = records.Single(r => r.Seq == lastSeq);

        var checkpoint = new AuditCheckpoint(
            FirstSeq: firstSeq,
            LastSeq: lastSeq,
            RowCount: lastSeq - firstSeq + 1,
            ChainHeadHash: head.Hash,
            MerkleRoot: Enumerable.Repeat(merkleFill, 32).ToArray(),
            CreatedAt: _t0.AddMinutes(lastSeq));
        return new StoredAuditCheckpoint(checkpoint, "audit-key-1", Enumerable.Repeat((byte)0x5A, 64).ToArray());
    }

    [Fact]
    public async Task Checkpoints_read_back_in_order_with_their_key_and_signature()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = await SeedAuditAsync(db, 8);
        var store = new PostgresAuditCheckpointStore(db.DataSource);

        var first = await CheckpointOverAsync(log, 1, 5);
        var second = await CheckpointOverAsync(log, 6, 8, merkleFill: 0x22);
        await store.AppendAsync(first, Ct);
        await store.AppendAsync(second, Ct);

        var all = new List<StoredAuditCheckpoint>();
        await foreach (var stored in store.ReadAllAsync(Ct))
            all.Add(stored);

        Assert.Equal(2, all.Count);
        Assert.Equal(first.Checkpoint, all[0].Checkpoint);
        Assert.Equal(second.Checkpoint, all[1].Checkpoint);
        Assert.Equal("audit-key-1", all[1].KeyId);
        Assert.Equal(second.Signature, all[1].Signature);

        var latest = await store.ReadLatestAsync(Ct);
        Assert.Equal(second.Checkpoint, latest!.Checkpoint);
    }

    [Fact]
    public async Task Empty_store_has_no_latest_checkpoint()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var store = new PostgresAuditCheckpointStore(db.DataSource);

        Assert.Null(await store.ReadLatestAsync(Ct));
    }

    [Fact]
    public async Task A_gap_before_the_next_checkpoint_is_rejected()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = await SeedAuditAsync(db, 8);
        var store = new PostgresAuditCheckpointStore(db.DataSource);
        await store.AppendAsync(await CheckpointOverAsync(log, 1, 5), Ct);

        // The next checkpoint must start at 6; starting at 7 would leave row 6 uncovered.
        var gapped = await CheckpointOverAsync(log, 7, 8);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(gapped, Ct));
        Assert.Contains("must start at seq 6", ex.Message);
        Assert.Single(await ReadAllAsync(store));
    }

    [Fact]
    public async Task A_checkpoint_whose_head_does_not_match_the_log_is_rejected_and_nothing_is_written()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = await SeedAuditAsync(db, 3);
        var store = new PostgresAuditCheckpointStore(db.DataSource);

        var honest = await CheckpointOverAsync(log, 1, 3);
        var forged = honest with
        {
            Checkpoint = honest.Checkpoint with { ChainHeadHash = Enumerable.Repeat((byte)0xEE, 32).ToArray() },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(forged, Ct));
        Assert.Contains("does not match the audit log at seq 3", ex.Message);
        Assert.Empty(await ReadAllAsync(store));
    }

    [Fact]
    public async Task A_checkpoint_beyond_the_end_of_the_log_is_rejected()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = await SeedAuditAsync(db, 3);
        var store = new PostgresAuditCheckpointStore(db.DataSource);

        var future = await CheckpointOverAsync(log, 1, 3);
        var beyond = future with
        {
            Checkpoint = future.Checkpoint with { LastSeq = 9, RowCount = 9 },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(beyond, Ct));
        Assert.Empty(await ReadAllAsync(store));
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("unaligned-time")]
    [InlineData("short-hash")]
    [InlineData("bad-row-count")]
    public async Task Malformed_checkpoints_are_rejected_before_any_write(string defect)
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = await SeedAuditAsync(db, 3);
        var store = new PostgresAuditCheckpointStore(db.DataSource);
        var good = await CheckpointOverAsync(log, 1, 3);

        var bad = defect switch
        {
            "unsigned" => good with { Signature = [] },
            "unaligned-time" => good with { Checkpoint = good.Checkpoint with { CreatedAt = good.Checkpoint.CreatedAt.AddTicks(1) } },
            "short-hash" => good with { Checkpoint = good.Checkpoint with { MerkleRoot = new byte[31] } },
            "bad-row-count" => good with { Checkpoint = good.Checkpoint with { RowCount = 2 } },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AppendAsync(bad, Ct));
        Assert.Empty(await ReadAllAsync(store));
    }

    private static async Task<List<StoredAuditCheckpoint>> ReadAllAsync(PostgresAuditCheckpointStore store)
    {
        var list = new List<StoredAuditCheckpoint>();
        await foreach (var stored in store.ReadAllAsync(Ct))
            list.Add(stored);
        return list;
    }
}
