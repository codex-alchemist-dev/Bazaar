using Bazaar.Testing.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.AuditSinks.Postgres.Tests;

public sealed class PostgresAuditSinkTests
{
    private static byte[] Hash(byte b) => Enumerable.Repeat(b, 32).ToArray();

    private static async Task InsertRowAsync(NpgsqlDataSource ds, long seq, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO audit_log (seq, ts, actor, kind, payload, prev_hash, hash) VALUES ($1, $2, 'a', 'k', CAST($3 AS jsonb), $4, $5)");
        cmd.Parameters.AddWithValue(seq);
        cmd.Parameters.AddWithValue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        cmd.Parameters.AddWithValue("{\"n\":" + seq + "}");
        cmd.Parameters.AddWithValue(Hash((byte)(seq - 1)));
        cmd.Parameters.AddWithValue(Hash((byte)seq));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertCheckpointAsync(NpgsqlDataSource ds, long first, long last, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO audit_checkpoint (first_seq, last_seq, row_count, chain_head_hash, merkle_root, signature, key_id, created_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, 'k1', $7)");
        cmd.Parameters.AddWithValue(first);
        cmd.Parameters.AddWithValue(last);
        cmd.Parameters.AddWithValue(last - first + 1);
        cmd.Parameters.AddWithValue(Hash((byte)last));
        cmd.Parameters.AddWithValue(Hash(0xEE));
        cmd.Parameters.AddWithValue(new byte[] { 1, 2, 3 });
        cmd.Parameters.AddWithValue(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public void The_di_extension_registers_both_adapters_and_keeps_a_callers_own()
    {
        var own = new FakeCursorStore();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditSinkCursorStore>(own);
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost"));
        services.AddBazaarAuditSinksPostgres();
        using var provider = services.BuildServiceProvider();

        Assert.Same(own, provider.GetRequiredService<IAuditSinkCursorStore>());
        Assert.IsType<PostgresAuditSinkSource>(provider.GetRequiredService<IAuditSinkSource>());
    }

    [Fact]
    public async Task The_source_refuses_a_non_positive_limit_before_touching_the_database()
    {
        using var ds = NpgsqlDataSource.Create("Host=localhost");
        var source = new PostgresAuditSinkSource(ds);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.ReadRowsAfterAsync(0, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.ReadCheckpointsAfterAsync(-1, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_cursor_store_refuses_an_empty_name_and_a_negative_cursor()
    {
        using var ds = NpgsqlDataSource.Create("Host=localhost");
        var store = new PostgresAuditSinkCursorStore(ds);
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync("", ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync("s", new AuditSinkCursor(-1, 0), ct));
    }

    [Fact]
    public async Task A_new_sink_starts_at_zero_and_a_saved_cursor_is_read_back()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, AuditSinksDatabase.Skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditSinksDatabase.CreateAsync(ct);
        var store = new PostgresAuditSinkCursorStore(db.DataSource);

        Assert.Equal(AuditSinkCursor.Start, await store.GetAsync("file", ct));
        await store.SaveAsync("file", new AuditSinkCursor(10, 2), ct);
        await store.SaveAsync("syslog", new AuditSinkCursor(3, 0), ct);

        Assert.Equal(new AuditSinkCursor(10, 2), await store.GetAsync("file", ct));
        Assert.Equal(new AuditSinkCursor(3, 0), await store.GetAsync("syslog", ct));
    }

    [Fact]
    public async Task Saving_never_moves_a_cursor_backwards()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, AuditSinksDatabase.Skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditSinksDatabase.CreateAsync(ct);
        var store = new PostgresAuditSinkCursorStore(db.DataSource);

        await store.SaveAsync("s", new AuditSinkCursor(10, 5), ct);
        await store.SaveAsync("s", new AuditSinkCursor(4, 9), ct);

        Assert.Equal(new AuditSinkCursor(10, 9), await store.GetAsync("s", ct));
    }

    [Fact]
    public async Task The_source_returns_rows_after_a_sequence_in_order_up_to_the_limit()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, AuditSinksDatabase.Skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditSinksDatabase.CreateAsync(ct);
        for (long i = 1; i <= 5; i++)
        {
            await InsertRowAsync(db.DataSource, i, ct);
        }

        var source = new PostgresAuditSinkSource(db.DataSource);
        var rows = await source.ReadRowsAfterAsync(1, 3, ct);

        Assert.Equal([2L, 3L, 4L], rows.Select(r => r.Seq));
        Assert.Equal("a", rows[0].Actor);
        Assert.Equal("k", rows[0].Kind);
        Assert.Contains("\"n\"", rows[0].Payload);
        Assert.Equal(Hash(1), rows[0].PrevHash);
        Assert.Equal(Hash(2), rows[0].Hash);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), rows[0].Ts.ToUniversalTime());
        Assert.Empty(await source.ReadRowsAfterAsync(5, 10, ct));
    }

    [Fact]
    public async Task The_source_returns_checkpoints_after_a_last_sequence_in_order()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, AuditSinksDatabase.Skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await AuditSinksDatabase.CreateAsync(ct);
        await InsertCheckpointAsync(db.DataSource, 1, 3, ct);
        await InsertCheckpointAsync(db.DataSource, 4, 6, ct);

        var source = new PostgresAuditSinkSource(db.DataSource);
        var all = await source.ReadCheckpointsAfterAsync(0, 10, ct);
        var after = await source.ReadCheckpointsAfterAsync(3, 10, ct);

        Assert.Equal([3L, 6L], all.Select(c => c.LastSeq));
        var cp = Assert.Single(after);
        Assert.Equal(4, cp.FirstSeq);
        Assert.Equal(3, cp.RowCount);
        Assert.Equal(Hash(6), cp.ChainHeadHash);
        Assert.Equal(Hash(0xEE), cp.MerkleRoot);
        Assert.Equal("k1", cp.KeyId);
        Assert.Equal(new byte[] { 1, 2, 3 }, cp.Signature);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), cp.CreatedAt.ToUniversalTime());
    }

    private sealed class FakeCursorStore : IAuditSinkCursorStore
    {
        public Task<AuditSinkCursor> GetAsync(string sinkName, CancellationToken ct) => Task.FromResult(AuditSinkCursor.Start);

        public Task SaveAsync(string sinkName, AuditSinkCursor cursor, CancellationToken ct) => Task.CompletedTask;
    }
}
