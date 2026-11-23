using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Host.Components.ReadAudit;
using Bazaar.Server.ReadAudit;
using Bazaar.Server.ReadAudit.Npgsql;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

/// <summary>The Hive stores read through <see cref="DbDataSource"/>, so the audited source records their reads (doc 48 section 5.1).</summary>
public sealed class HiveReadAuditTests
{
    private sealed class Log : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }

        public List<JsonObject> Payloads() => Entries.Select(e => JsonNode.Parse(e.Payload)!.AsObject()).ToList();
    }

    private static DataTable Grants()
    {
        var table = new DataTable();
        foreach (var column in new[] { "from_project", "to_project", "kind", "granted_by" })
            table.Columns.Add(column, typeof(string));
        table.Rows.Add("a", "b", "procedure", "owner");
        table.Rows.Add("a", "c", "failure", "owner");
        return table;
    }

    private static (AuditedDbDataSource Audited, Log Log, FakeSource Inner) Audited()
    {
        var log = new Log();
        var inner = new FakeSource(Grants());
        var auditor = new ReadAuditor(log, TimeProvider.System);
        return (new AuditedDbDataSource(inner, auditor), log, inner);
    }

    [Fact]
    public async Task Grant_list_through_the_audited_source_is_recorded_under_hive_grant()
    {
        var (audited, log, inner) = Audited();

        var grants = await new PgHiveGrantStore(audited).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, grants.Count);
        Assert.Single(inner.Executed);
        var row = Assert.Single(log.Payloads());
        Assert.Equal("hive_grant", (string?)row["object_type"]);
        Assert.Equal(2, (int)row["result_count"]!);
        Assert.Null(row["object_id"]);
    }

    [Fact]
    public async Task Grant_list_through_a_raw_source_still_works_and_records_nothing()
    {
        // The same store with the raw source: the read declaration is skipped instead of throwing.
        var inner = new FakeSource(Grants());

        var grants = await new PgHiveGrantStore(inner).ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, grants.Count);
    }

    [Fact]
    public async Task Pending_and_copy_lists_declare_their_own_object_types()
    {
        var (audited, log, inner) = Audited();
        inner.Rows = new DataTable();
        foreach (var column in new[] { "id", "origin_project", "origin_note", "kind", "target_project", "slug", "title", "body", "origin_tier" })
            inner.Rows.Columns.Add(column, typeof(string));
        var ledger = new PgHiveLedger(audited);

        Assert.Empty(await ledger.ListPendingAsync(TestContext.Current.CancellationToken));
        inner.Rows = new DataTable();
        foreach (var (column, type) in new (string, Type)[]
            { ("path", typeof(string)), ("origin_project", typeof(string)), ("origin_note", typeof(string)), ("kind", typeof(string)),
              ("target_project", typeof(string)), ("revoked", typeof(bool)), ("seq", typeof(long)) })
            inner.Rows.Columns.Add(column, type);
        Assert.Empty(await ledger.ListCopiesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["hive_held_copy", "hive_copy"], log.Payloads().Select(p => (string)p["object_type"]!).ToArray());
    }

    private static ServiceProvider Build(bool readAudit)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused"));
        if (readAudit)
        {
            services.AddSingleton<IAuditLog>(new Log());
            new ReadAuditComponent().AddServices(services, new ConfigurationBuilder().Build());
        }

        new HiveComponent().AddServices(services, new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static DbDataSource SourceOf(object store) =>
        (DbDataSource)store.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(f => typeof(DbDataSource).IsAssignableFrom(f.FieldType)).GetValue(store)!;

    [Fact]
    public void Stores_get_the_audited_source_when_the_read_audit_component_is_on()
    {
        using var provider = Build(readAudit: true);

        Assert.IsType<AuditedDbDataSource>(SourceOf(provider.GetRequiredService<IHiveGrantStore>()));
        Assert.IsType<AuditedDbDataSource>(SourceOf(provider.GetRequiredService<IHiveCopyLedger>()));
    }

    [Fact]
    public void Stores_get_the_raw_source_when_the_read_audit_component_is_off()
    {
        using var provider = Build(readAudit: false);

        Assert.IsAssignableFrom<NpgsqlDataSource>(SourceOf(provider.GetRequiredService<IHiveGrantStore>()));
        Assert.IsAssignableFrom<NpgsqlDataSource>(SourceOf(provider.GetRequiredService<IHiveCopyLedger>()));
    }

    private sealed class FakeSource(DataTable rows) : DbDataSource
    {
        public DataTable Rows { get; set; } = rows;

        public List<string> Executed { get; } = [];

        public override string ConnectionString => "fake";

        protected override DbConnection CreateDbConnection() => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand(string? commandText = null) => new FakeCommand(this) { CommandText = commandText! };
    }

    private sealed class FakeCommand(FakeSource source) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            source.Executed.Add(CommandText);
            return source.Rows.CreateDataReader();
        }
    }
}
