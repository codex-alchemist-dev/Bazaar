using Bazaar.Server.Constellation.Memory.ReadLog;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Constellation.Tests.Memory.ReadLog;

/// <summary>The owner-read log on real PostgreSQL: rows, the audit chain link, atomicity, paging and append-only enforcement.</summary>
public sealed class PgMemoryReadLogTests(ReadLogDb db) : IClassFixture<ReadLogDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static MemoryReadEntry Entry(string subject, int count = 2, string reader = "owner") => new(
        reader,
        "sess-1",
        "memory.context",
        subject,
        MemoryReadOperations.Retrieve,
        [.. Enumerable.Range(0, count).Select(i => $"note-{i}")],
        [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7().ToString("D"))],
        ["labelled_quoted_data"],
        "review");

    private PgMemoryReadLog NewLog(TimeProvider? time = null, int? maxIds = null, ITransactionalAuditLog? audit = null) =>
        new(db.DataSource, audit ?? new PostgresAuditLog(db.DataSource), time, maxIds is null ? null : _ => Task.FromResult(maxIds.Value));

    [Fact]
    public async Task Record_writes_a_row_and_a_chained_audit_entry_in_one_transaction()
    {
        SkipWithoutDatabase();
        string subject = "agent-" + Guid.NewGuid().ToString("N");
        var entry = Entry(subject);

        await NewLog().RecordAsync([entry], Ct);

        MemoryReadLogRecord row = Assert.Single((await NewLog().ListAsync(new MemoryReadLogQuery(subject), Ct)).Records);
        Assert.Equal(26, row.ReadId.Length);
        Assert.Equal("owner", row.ReaderPrincipal);
        Assert.Equal("sess-1", row.ReaderSession);
        Assert.Equal("memory.context", row.Route);
        Assert.Equal(subject, row.SubjectAgentId);
        Assert.Equal("retrieve", row.Operation);
        Assert.Equal(entry.NoteIds, row.NoteIds);
        Assert.Equal(entry.DocIds, row.DocIds);
        Assert.Equal(2, row.ResultCount);
        Assert.False(row.IdsTruncated);
        Assert.Equal(["labelled_quoted_data"], row.TaintTags);
        Assert.Equal("review", row.Purpose);

        var audit = new PostgresAuditLog(db.DataSource);
        AuditRecord linked = await audit.StreamAsync(Ct).SingleAsync(r => r.Seq == row.AuditSeq, Ct);
        Assert.Equal("memory.owner_read", linked.Kind);
        Assert.Equal("owner", linked.Actor);
        Assert.Contains(row.ReadId, linked.Payload);
        Assert.True((await audit.VerifyAsync(null, Ct)).IsValid);
    }

    [Fact]
    public async Task Rows_hold_ids_only()
    {
        SkipWithoutDatabase();
        string subject = "agent-" + Guid.NewGuid().ToString("N");
        await NewLog().RecordAsync([Entry(subject)], Ct);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'constellation_memory_read_log'");
        var columns = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.DoesNotContain(columns, c => c.Contains("body", StringComparison.Ordinal)
            || c.Contains("title", StringComparison.Ordinal) || c.Contains("path", StringComparison.Ordinal)
            || c.Contains("query", StringComparison.Ordinal) || c.Contains("text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_larger_read_stores_the_first_ids_the_full_count_and_the_truncated_flag()
    {
        SkipWithoutDatabase();
        string subject = "agent-" + Guid.NewGuid().ToString("N");

        await NewLog(maxIds: 3).RecordAsync([Entry(subject, count: 10)], Ct);

        MemoryReadLogRecord row = Assert.Single((await NewLog().ListAsync(new MemoryReadLogQuery(subject), Ct)).Records);
        Assert.Equal(3, row.NoteIds.Count);
        Assert.Equal(3, row.DocIds.Count);
        Assert.Equal(10, row.ResultCount);
        Assert.True(row.IdsTruncated);
    }

    [Fact]
    public async Task Failed_audit_append_rolls_the_row_back_and_refuses_with_the_log_exception()
    {
        SkipWithoutDatabase();
        string subject = "agent-" + Guid.NewGuid().ToString("N");
        var failing = new FailingAudit(new PostgresAuditLog(db.DataSource));

        await Assert.ThrowsAsync<MemoryReadLogException>(() => NewLog(audit: failing).RecordAsync([Entry(subject)], Ct));

        Assert.Empty((await NewLog().ListAsync(new MemoryReadLogQuery(subject), Ct)).Records);
        Assert.Empty(await new PostgresAuditLog(db.DataSource).StreamAsync(Ct).Where(r => r.Payload.Contains(subject, StringComparison.Ordinal)).ToListAsync(Ct));
    }

    [Fact]
    public async Task Failing_database_refuses_with_the_log_exception()
    {
        SkipWithoutDatabase();
        await using var broken = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=1;Command Timeout=1");
        var log = new PgMemoryReadLog(broken, new PostgresAuditLog(broken));

        await Assert.ThrowsAsync<MemoryReadLogException>(() => log.RecordAsync([Entry("agent-x")], Ct));
    }

    [Fact]
    public async Task Unknown_operation_is_rejected_before_anything_is_written()
    {
        SkipWithoutDatabase();
        MemoryReadEntry bad = Entry("agent-x") with { Operation = "peek" };

        await Assert.ThrowsAsync<ArgumentException>(() => NewLog().RecordAsync([bad], Ct));
    }

    [Fact]
    public async Task Reader_pages_newest_first_and_filters_by_agent()
    {
        SkipWithoutDatabase();
        string agentA = "agent-a-" + Guid.NewGuid().ToString("N");
        string agentB = "agent-b-" + Guid.NewGuid().ToString("N");
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var log = NewLog(clock);
        for (int i = 0; i < 5; i++)
        {
            await log.RecordAsync([Entry(agentA, reader: $"r{i}")], Ct);
            clock.Advance(TimeSpan.FromSeconds(1));
            await log.RecordAsync([Entry(agentB, reader: $"s{i}")], Ct);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        MemoryReadLogPage first = await log.ListAsync(new MemoryReadLogQuery(agentA, Limit: 2), Ct);
        Assert.Equal(["r4", "r3"], first.Records.Select(r => r.ReaderPrincipal).ToArray());
        Assert.NotNull(first.Next);

        MemoryReadLogPage second = await log.ListAsync(new MemoryReadLogQuery(agentA, first.Next, 2), Ct);
        Assert.Equal(["r2", "r1"], second.Records.Select(r => r.ReaderPrincipal).ToArray());

        MemoryReadLogPage last = await log.ListAsync(new MemoryReadLogQuery(agentA, second.Next, 2), Ct);
        Assert.Equal(["r0"], last.Records.Select(r => r.ReaderPrincipal).ToArray());
        Assert.Null(last.Next);
        Assert.All(first.Records.Concat(second.Records).Concat(last.Records), r => Assert.Equal(agentA, r.SubjectAgentId));

        // Without the filter both agents' reads come back, still newest first.
        MemoryReadLogPage all = await log.ListAsync(new MemoryReadLogQuery(Limit: 200), Ct);
        Assert.Equal(all.Records.OrderByDescending(r => r.At).ThenByDescending(r => r.ReadId, StringComparer.Ordinal).ToArray(), all.Records);
    }

    [Fact]
    public async Task Runtime_role_can_insert_and_select_but_holds_no_update_or_delete_grant()
    {
        SkipWithoutDatabase();
        Assert.True(await HasPrivilegeAsync("SELECT"));
        Assert.True(await HasPrivilegeAsync("INSERT"));
        Assert.False(await HasPrivilegeAsync("UPDATE"));
        Assert.False(await HasPrivilegeAsync("DELETE"));
        Assert.False(await HasPrivilegeAsync("TRUNCATE"));

        string subject = "agent-" + Guid.NewGuid().ToString("N");
        await using var app = db.AsRole("bazaar_app");
        var asApp = new PgMemoryReadLog(app, new PostgresAuditLog(app));
        await asApp.RecordAsync([Entry(subject)], Ct);
        Assert.Single((await asApp.ListAsync(new MemoryReadLogQuery(subject), Ct)).Records);

        foreach (string sql in new[] { "UPDATE constellation_memory_read_log SET purpose = 'x'", "DELETE FROM constellation_memory_read_log", "TRUNCATE constellation_memory_read_log" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = app.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync(Ct);
            });
            Assert.Equal("42501", ex.SqlState);
        }

        Assert.Single((await asApp.ListAsync(new MemoryReadLogQuery(subject), Ct)).Records);
    }

    [Fact]
    public async Task Triggers_refuse_update_delete_and_truncate_even_for_the_superuser()
    {
        SkipWithoutDatabase();
        await NewLog().RecordAsync([Entry("agent-" + Guid.NewGuid().ToString("N"))], Ct);

        foreach (string sql in new[] { "UPDATE constellation_memory_read_log SET purpose = 'x'", "DELETE FROM constellation_memory_read_log", "TRUNCATE constellation_memory_read_log" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = db.DataSource.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync(Ct);
            });
            Assert.Contains("append only", ex.MessageText, StringComparison.Ordinal);
        }
    }

    private async Task<bool> HasPrivilegeAsync(string privilege)
    {
        await using var cmd = db.DataSource.CreateCommand($"SELECT has_table_privilege('bazaar_app', 'constellation_memory_read_log', '{privilege}')");
        return (bool)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Appends to the real chain, then fails, so the surrounding transaction must roll the append back.</summary>
    private sealed class FailingAudit(PostgresAuditLog inner) : ITransactionalAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => inner.AppendAsync(e, ct);

        public async Task AppendInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e, CancellationToken ct = default)
        {
            await inner.AppendInTransactionAsync(conn, tx, e, ct);
            throw new InvalidOperationException("audit append failed after the chain write");
        }
    }
}
