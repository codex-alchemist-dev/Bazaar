using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.Audit.Db;

/// <summary>PostgresAuditLog against a real PostgreSQL: append, read back, verify, export (doc 15).</summary>
public sealed class AuditAppendTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static async Task<List<AuditRecord>> ReadAllAsync(PostgresAuditLog log)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in log.StreamAsync(Ct))
            records.Add(record);
        return records;
    }

    [Fact]
    public async Task Appended_rows_read_back_as_a_chain_that_verifies()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);

        for (var i = 0; i < 5; i++)
            await log.AppendAsync(new AuditEntry(_t0.AddSeconds(i), "agent:charles", "tool.call", $"{{\"n\": {i}}}"), Ct);

        var records = await ReadAllAsync(log);
        Assert.Equal([1L, 2, 3, 4, 5], records.Select(r => r.Seq));
        Assert.Equal(AuditChain.GenesisHash, records[0].PrevHash);
        Assert.Equal(records[0].Hash, records[1].PrevHash);

        var verification = await log.VerifyAsync(ct: Ct);
        Assert.True(verification.IsValid);
        Assert.Equal(5, verification.Checked);

        var head = await log.ReadHeadAsync(Ct);
        Assert.Equal(records[4].Hash, head!.Hash);
        Assert.True((await log.VerifyAsync(head.Hash, Ct)).IsValid);
    }

    [Fact]
    public async Task Empty_log_has_no_head_and_verifies_trivially()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);

        Assert.Null(await log.ReadHeadAsync(Ct));
        var verification = await log.VerifyAsync(ct: Ct);
        Assert.True(verification.IsValid);
        Assert.Equal(0, verification.Checked);
    }

    [Fact]
    public async Task Payload_is_stored_in_canonical_jsonb_form_and_still_verifies()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);

        // Postgres reorders keys and normalises spacing; the hash must cover the form that is read back.
        await log.AppendAsync(new AuditEntry(_t0, "owner", "config.change", "{\"b\":1,   \"a\":[1,2]}"), Ct);

        var record = (await ReadAllAsync(log)).Single();
        Assert.Equal("{\"a\": [1, 2], \"b\": 1}", record.Payload);
        Assert.True((await log.VerifyAsync(ct: Ct)).IsValid);
    }

    [Fact]
    public async Task Sub_microsecond_timestamps_still_verify()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);

        // Ticks are 100 ns; Postgres keeps microseconds. The writer hashes the stored precision.
        var ts = new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        await log.AppendAsync(new AuditEntry(ts, "owner", "kill.switch", "{}"), Ct);
        await log.AppendAsync(new AuditEntry(ts.AddTicks(7), "owner", "kill.switch", "{}"), Ct);

        var records = await ReadAllAsync(log);
        Assert.Equal(ts.UtcTicks - ts.UtcTicks % 10, records[0].Ts.UtcTicks);
        Assert.True((await log.VerifyAsync(ct: Ct)).IsValid);
    }

    [Fact]
    public async Task Payload_that_is_not_json_is_refused_and_leaves_no_row()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => log.AppendAsync(new AuditEntry(_t0, "owner", "tool.call", "not json"), Ct));
        Assert.Empty(await ReadAllAsync(log));
    }

    [Fact]
    public async Task Eight_parallel_writers_keep_one_valid_chain_without_gaps()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);

        var writes = Enumerable.Range(0, 8).Select(writer => Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
                await log.AppendAsync(new AuditEntry(_t0.AddMilliseconds(writer * 10 + i), $"agent:{writer}", "tool.call", $"{{\"i\": {i}}}"), Ct);
        }, Ct)).ToArray();
        await Task.WhenAll(writes);

        var records = await ReadAllAsync(log);
        Assert.Equal(40, records.Count);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), records.Select(r => r.Seq));

        var verification = await log.VerifyAsync(ct: Ct);
        Assert.True(verification.IsValid, $"chain broke at seq {verification.FailedSeq}: {verification.Failure}");
    }

    [Fact]
    public async Task Export_round_trip_parses_back_and_verifies_against_the_head()
    {
        SkipWithoutDatabase();
        await using var db = await AuditTestDb.CreateAsync(Ct);
        var log = new PostgresAuditLog(db.DataSource);
        for (var i = 0; i < 3; i++)
            await log.AppendAsync(new AuditEntry(_t0.AddSeconds(i), "owner", "approval.decide", $"{{\"ref\": {i}}}"), Ct);

        var text = await log.ExportAsync(Ct);
        var parsed = AuditExport.FromJsonLines(text);
        var stored = await ReadAllAsync(log);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(stored.Select(r => r.Hash), parsed.Select(r => r.Hash));
        Assert.Equal(stored.Select(r => r.Payload), parsed.Select(r => r.Payload));
        Assert.True(AuditChain.Verify(parsed, stored[^1].Hash).IsValid);
    }
}
