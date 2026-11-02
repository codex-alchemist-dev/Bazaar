using System.Text.Json;
using Bazaar.Server.Vms.Reconciliation;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Vms.Tests.Reconciliation;

/// <summary>The Postgres audit writer against a real database: columns, hash chain, concurrency, input checks and append-only.</summary>
public sealed class PgAuditWriterTests : ReconcileDb
{
    [Fact]
    public async Task Writes_action_subject_detail_and_time_into_the_audit_table()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var subject = Guid.CreateVersion7();
        var at = Epoch.AddMilliseconds(250);

        await AuditWriter.WriteAsync(
            new AuditRecord(VmAuditActions.StateMismatch, subject, """{"recorded":"Stopped","actual":"Running"}""", at),
            TestContext.Current.CancellationToken);

        var rows = await ReadAuditAsync();
        var row = Assert.Single(rows);
        Assert.Equal(1, row.Seq);
        Assert.Equal(VmAuditActions.StateMismatch, row.Kind);
        Assert.Equal("vm-component", row.Actor);
        Assert.Equal(at, row.Ts);

        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(subject.ToString(), payload.RootElement.GetProperty("subject").GetString());
        Assert.Equal("Stopped", payload.RootElement.GetProperty("detail").GetProperty("recorded").GetString());
        Assert.Equal("Running", payload.RootElement.GetProperty("detail").GetProperty("actual").GetString());
        Assert.Equal(32, row.Hash.Length);
        Assert.Equal(new byte[32], row.PrevHash);
    }

    [Fact]
    public async Task Each_row_links_to_the_previous_hash_and_every_hash_recomputes()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var subject = Guid.CreateVersion7();
        for (var i = 0; i < 3; i++)
            await AuditWriter.WriteAsync(
                new AuditRecord(VmAuditActions.OrphanBackendVm, subject, $$"""{"n":{{i}}}""", Epoch.AddSeconds(i)),
                TestContext.Current.CancellationToken);

        var rows = await ReadAuditAsync();

        Assert.Equal(new long[] { 1, 2, 3 }, rows.Select(r => r.Seq));
        Assert.Equal(rows[0].Hash, rows[1].PrevHash);
        Assert.Equal(rows[1].Hash, rows[2].PrevHash);
        Assert.True(ChainIsValid(rows));
    }

    [Fact]
    public async Task Concurrent_appenders_produce_one_contiguous_valid_chain()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var writes = Enumerable.Range(0, 20).Select(i => AuditWriter.WriteAsync(
            new AuditRecord(VmAuditActions.BackendError, Guid.CreateVersion7(), $$"""{"i":{{i}}}""", Epoch),
            TestContext.Current.CancellationToken));

        await Task.WhenAll(writes);

        var rows = await ReadAuditAsync();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), rows.Select(r => r.Seq));
        Assert.True(ChainIsValid(rows));
    }

    [Fact]
    public async Task Rejects_detail_that_is_not_a_json_document_and_writes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        foreach (var detail in new[] { "not json", "", "null" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                AuditWriter.WriteAsync(new AuditRecord(VmAuditActions.BackendError, Guid.CreateVersion7(), detail, Epoch), ct));
        }

        Assert.Empty(await ReadAuditAsync());
    }

    [Fact]
    public async Task Rejects_an_empty_action()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AuditWriter.WriteAsync(new AuditRecord("", Guid.CreateVersion7(), "{}", Epoch), TestContext.Current.CancellationToken));

        Assert.Empty(await ReadAuditAsync());
    }

    [Fact]
    public async Task Audit_rows_cannot_be_updated_or_deleted()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await AuditWriter.WriteAsync(new AuditRecord(VmAuditActions.BackendError, Guid.CreateVersion7(), "{}", Epoch), ct);

        await using (var update = DataSource.CreateCommand("UPDATE audit_log SET actor = 'changed'"))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(ct));
            Assert.Equal("42501", ex.SqlState);
        }

        await using (var delete = DataSource.CreateCommand("DELETE FROM audit_log"))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(ct));
            Assert.Equal("42501", ex.SqlState);
        }

        Assert.Equal("vm-component", Assert.Single(await ReadAuditAsync()).Actor);
    }
}
