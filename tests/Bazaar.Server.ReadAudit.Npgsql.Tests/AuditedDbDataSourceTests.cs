using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.ReadAudit.Npgsql.Tests;

public sealed class AuditedDbDataSourceTests
{
    private static readonly ReadAccessContext _ctx = new("agent:alice", "tool/gc.read", Purpose: "triage");

    private sealed class Rig
    {
        public FakeDataSource Inner { get; } = new();

        public RecordingLog Log { get; } = new();

        public ReadAuditor Auditor { get; }

        public AuditedDbDataSource Source { get; }

        public Rig(AuditedDataSourceOptions? options = null)
        {
            Auditor = new ReadAuditor(Log, new FixedClock(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero)));
            Source = new AuditedDbDataSource(Inner, Auditor, options);
        }
    }


    private static async Task Drain(DbDataReader r)
    {
        while (await r.ReadAsync(TestContext.Current.CancellationToken)) { }
    }

    [Fact]
    public async Task Reader_records_one_row_with_the_count_when_it_closes()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"), (2, "b"), (3, "c"));
        await using var cmd = rig.Source.CreateCommand("select id, name from gc_member where gc = 7").DeclareRead("gc_member");
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            await Drain(reader);
            Assert.Empty(rig.Log.Entries);
        }

        var row = Assert.Single(rig.Log.Payloads());
        Assert.Equal("gc_member", (string?)row["object_type"]);
        Assert.Equal(3, (int)row["result_count"]!);
        Assert.Equal("select", (string?)row["query_kind"]);
        Assert.Null(row["object_id"]);
        Assert.Equal("unattributed", (string?)row["principal"]);
    }

    [Fact]
    public async Task Sync_reader_dispose_records_too()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"), (2, "b"));
        using var cmd = rig.Source.CreateCommand("select id from t").DeclareRead("t");
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) { }
        }

        Assert.Equal(2, (int)Assert.Single(rig.Log.Payloads())["result_count"]!);
    }

    [Fact]
    public async Task Reader_values_pass_through()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((5, "zed"));
        await using var cmd = rig.Source.CreateCommand("select id, name from t").DeclareRead("t");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, reader.GetInt32(0));
        Assert.Equal("zed", reader.GetString(reader.GetOrdinal("name")));
        Assert.Equal(2, reader.FieldCount);
    }

    [Fact]
    public async Task Personal_declaration_records_one_row_per_id()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((11, "a"), (12, "b"));
        await using var cmd = rig.Source.CreateCommand("select id, name from person").DeclareRead("person", "id");
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);

        var rows = rig.Log.Payloads();
        Assert.Equal(2, rows.Count);
        Assert.Equal(["11", "12"], rows.Select(r => (string?)r["object_id"]));
        Assert.All(rows, r => Assert.Equal(1, (int)r["result_count"]!));
    }

    [Fact]
    public async Task Scalar_records_a_count_of_one_or_zero()
    {
        var rig = new Rig();
        rig.Inner.Scalar = 42;
        await using (var cmd = rig.Source.CreateCommand("select count(*) from t").DeclareRead("t"))
            Assert.Equal(42, await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        rig.Inner.Scalar = null;
        using (var cmd = rig.Source.CreateCommand("select max(id) from t").DeclareRead("t"))
            Assert.Null(cmd.ExecuteScalar());

        Assert.Equal([1, 0], rig.Log.Payloads().Select(p => (int)p["result_count"]!));
    }

    [Theory]
    [InlineData("insert into t(a) values (1)")]
    [InlineData("update t set a = 1")]
    [InlineData("delete from t")]
    public async Task Writes_are_not_audited(string sql)
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"));
        await using var cmd = rig.Source.CreateCommand(sql);
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task Execute_non_query_is_never_audited()
    {
        var rig = new Rig();
        await using var cmd = rig.Source.CreateCommand("select 1");
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task Untagged_reads_are_recorded_as_unclassified_by_default()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"));
        await using var cmd = rig.Source.CreateCommand("select 1");
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);
        Assert.Equal("unclassified", (string?)Assert.Single(rig.Log.Payloads())["object_type"]);
    }

    [Fact]
    public async Task Untagged_reads_can_be_skipped_by_option()
    {
        var rig = new Rig(new AuditedDataSourceOptions { RecordUntaggedReads = false });
        rig.Inner.Rows = FakeDataSource.Table((1, "a"));
        await using var cmd = rig.Source.CreateCommand("select 1");
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);
        Assert.Empty(rig.Log.Entries);
        await using var tagged = rig.Source.CreateCommand("select 1").DeclareRead("t");
        await using (var reader = await tagged.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);
        Assert.Single(rig.Log.Entries);
    }

    [Fact]
    public void Invalid_untagged_object_type_is_rejected_at_construction()
    {
        var rig = new Rig();
        Assert.Throws<ArgumentException>(() => new AuditedDbDataSource(rig.Inner, rig.Auditor, new AuditedDataSourceOptions { UntaggedObjectType = "Bad Label" }));
    }

    [Fact]
    public async Task Skip_read_audit_excludes_the_command()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"));
        await using var cmd = rig.Source.CreateCommand("select * from audit_log").SkipReadAudit();
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task Bypass_scope_excludes_reads_and_ends_with_its_scope()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"));
        using (ReadAuditBypass.Suppress())
        {
            Assert.True(ReadAuditBypass.IsActive);
            await using var cmd = rig.Source.CreateCommand("select 1").DeclareRead("t");
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            await Drain(reader);
        }

        Assert.False(ReadAuditBypass.IsActive);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task Unaudited_source_is_the_inner_source()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"));
        await using var cmd = rig.Source.Unaudited.CreateCommand("select 1");
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);
        Assert.Empty(rig.Log.Entries);
        Assert.Single(rig.Inner.Executed);
    }

    [Fact]
    public async Task Recording_a_read_does_not_record_reads_the_log_makes_itself()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"));
        // The log reads through the audited source while it appends, as a log migrated to the wrapper would.
        rig.Log.OnAppend = async () =>
        {
            await using var cmd = rig.Source.CreateCommand("select head from audit_log").DeclareRead("audit_log");
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            await Drain(reader);
        };
        await using var outer = rig.Source.CreateCommand("select 1").DeclareRead("t");
        await using (var reader = await outer.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);

        Assert.Equal("t", (string?)Assert.Single(rig.Log.Payloads())["object_type"]);
        Assert.False(ReadAuditBypass.IsActive);
    }

    [Fact]
    public async Task Open_connection_commands_are_audited()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"), (2, "b"));
        await using var conn = await rig.Source.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select id from t";
        cmd.DeclareRead("t");
        Assert.Same(conn, cmd.Connection);
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);
        Assert.Equal(2, (int)Assert.Single(rig.Log.Payloads())["result_count"]!);
    }

    [Fact]
    public async Task Reads_inside_a_scope_batch_by_shape_and_attribute_to_the_scope()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "a"), (2, "b"));
        await using (rig.Auditor.BeginScope(_ctx))
        {
            for (var i = 0; i < 2; i++)
            {
                await using var cmd = rig.Source.CreateCommand($"select id from t where x = {i}").DeclareRead("t");
                await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                await Drain(reader);
            }

            Assert.Empty(rig.Log.Entries);
        }

        var row = Assert.Single(rig.Log.Payloads());
        Assert.Equal(4, (int)row["result_count"]!);
        Assert.Equal("agent:alice", (string?)row["principal"]);
        Assert.Equal("tool/gc.read", (string?)row["route"]);
    }

    [Fact]
    public async Task Persisted_rows_hold_no_sql_and_no_shape_hash()
    {
        var rig = new Rig();
        rig.Inner.Rows = FakeDataSource.Table((1, "secret-name"));
        const string sql = "select name from person where email = 'x@example.org'";
        await using var cmd = rig.Source.CreateCommand(sql).DeclareRead("person");
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            await Drain(reader);

        var text = Assert.Single(rig.Log.Entries).Payload;
        Assert.DoesNotContain("example.org", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-name", text, StringComparison.Ordinal);
        Assert.DoesNotContain(QueryShape.Hash(sql), text, StringComparison.Ordinal);
        Assert.DoesNotContain("shape", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Flush_failure_propagates_from_reader_dispose()
    {
        var inner = new FakeDataSource { Rows = FakeDataSource.Table((1, "a")) };
        var source = new AuditedDbDataSource(inner, new ReadAuditor(new ThrowingLog(), TimeProvider.System));
        await using var cmd = source.CreateCommand("select 1").DeclareRead("t");
        var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await Drain(reader);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.DisposeAsync());
    }

    [Fact]
    public void Flush_failure_propagates_from_scalar()
    {
        var inner = new FakeDataSource { Scalar = 1 };
        var source = new AuditedDbDataSource(inner, new ReadAuditor(new ThrowingLog(), TimeProvider.System));
        using var cmd = source.CreateCommand("select 1").DeclareRead("t");
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Personal_row_with_a_null_id_fails_closed()
    {
        var rig = new Rig();
        var table = FakeDataSource.Table();
        table.Columns["name"]!.AllowDBNull = true;
        table.Columns["id"]!.AllowDBNull = true;
        table.Rows.Add(DBNull.Value, "a");
        rig.Inner.Rows = table;
        await using var cmd = rig.Source.CreateCommand("select id from person").DeclareRead("person", "id");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Batches_are_refused()
    {
        var rig = new Rig();
        Assert.Throws<NotSupportedException>(() => rig.Source.CreateBatch());
    }

    [Fact]
    public void Declare_on_a_foreign_command_is_rejected()
    {
        var rig = new Rig();
        using var raw = rig.Inner.CreateCommand("select 1");
        Assert.Throws<ArgumentException>(() => raw.DeclareRead("t"));
        Assert.Throws<ArgumentException>(() => raw.SkipReadAudit());
    }

    [Fact]
    public void Connection_string_comes_from_the_inner_source()
    {
        Assert.Equal("fake", new Rig().Source.ConnectionString);
    }

    [Fact]
    public void Di_extension_wraps_the_registered_npgsql_source_and_keeps_it()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Bazaar.Server.Security.Audit.IAuditLog>(new RecordingLog());
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=x"));
        services.AddBazaarAuditedDataSource(o => o.UntaggedObjectType = "legacy");
        using var provider = services.BuildServiceProvider();

        var audited = provider.GetRequiredService<AuditedDbDataSource>();
        Assert.Same(audited, provider.GetRequiredService<AuditedDbDataSource>());
        Assert.Same(provider.GetRequiredService<NpgsqlDataSource>(), audited.Unaudited);
        Assert.Equal("legacy", provider.GetRequiredService<AuditedDataSourceOptions>().UntaggedObjectType);
        Assert.NotNull(provider.GetRequiredService<ReadAuditor>());
    }
}
