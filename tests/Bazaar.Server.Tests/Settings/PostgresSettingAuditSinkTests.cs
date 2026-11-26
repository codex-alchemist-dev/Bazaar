using System.Text.Json;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Settings;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Settings;

/// <summary>Settings writes audited into the real Postgres audit_log (migration 0004), checked with the chain verifier.</summary>
public sealed class PostgresSettingAuditSinkTests(SettingsDb db) : IClassFixture<SettingsDb>
{
    private static readonly SettingActor _operator = new("operator:dom", SettingRole.Operator);

    private static async Task<(NpgsqlDataSource Data, PostgresAuditLog Log, NpgsqlSettingStore Store)> FreshAsync(SettingsDb db)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using (var cmd = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
            await cmd.ExecuteNonQueryAsync(ct);

        var ds = db.DataSource;
        await new MigrationRunner(ds).MigrateAsync(new EmbeddedMigrationSource(typeof(NpgsqlSettingStore).Assembly), "linux", ct);
        var log = new PostgresAuditLog(ds);
        return (ds, log, new NpgsqlSettingStore(ds, new PostgresSettingAuditSink(log)));
    }

    private static async Task<List<(string Kind, JsonDocument Payload)>> RowsAsync(NpgsqlDataSource ds)
    {
        var rows = new List<(string Kind, JsonDocument Payload)>();
        await using var cmd = ds.CreateCommand("SELECT kind, payload::text FROM audit_log ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            rows.Add((reader.GetString(0), JsonDocument.Parse(reader.GetString(1))));
        return rows;
    }

    [Fact]
    public async Task Accepted_change_appends_exactly_one_setting_changed_row_and_the_chain_verifies()
    {
        var (ds, log, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Accepted, result.Outcome);
        var (kind, _) = Assert.Single(await RowsAsync(ds));
        Assert.Equal(PostgresSettingAuditSink.ChangedKind, kind);
        Assert.True((await log.VerifyAsync(ct: ct)).IsValid);
    }

    [Fact]
    public async Task Payload_holds_key_old_new_and_actor_as_valid_json()
    {
        var (ds, _, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, cancellationToken: ct);

        var (kind, payload) = Assert.Single(await RowsAsync(ds));
        Assert.Equal(PostgresSettingAuditSink.ChangedKind, kind);
        var root = payload.RootElement;
        Assert.Equal("host.shutdown_timeout_seconds", root.GetProperty("key").GetString());
        Assert.Equal("operator:dom", root.GetProperty("actor").GetString());
        Assert.Equal("accepted", root.GetProperty("outcome").GetString());
        Assert.Equal(20, root.GetProperty("old").GetInt32());
        Assert.Equal(30, root.GetProperty("new").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Rejected_loosening_writes_no_setting_changed_row_and_a_rejected_row_that_verifies()
    {
        var (ds, log, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ReadinessRequiredRoles, ["search", "service_host"], _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        var rows = await RowsAsync(ds);
        Assert.DoesNotContain(rows, r => r.Kind == PostgresSettingAuditSink.ChangedKind);
        var (kind, payload) = Assert.Single(rows);
        Assert.Equal(PostgresSettingAuditSink.RejectedKind, kind);
        Assert.Contains("postgres", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal("host.readiness.required_roles", payload.RootElement.GetProperty("key").GetString());
        Assert.True((await log.VerifyAsync(ct: ct)).IsValid);
    }

    [Fact]
    public async Task Held_change_writes_a_setting_held_row()
    {
        var (ds, log, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.StatusBindAddress, StatusBind.Lan, _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.PendingApproval, result.Outcome);
        var (kind, payload) = Assert.Single(await RowsAsync(ds));
        Assert.Equal(PostgresSettingAuditSink.HeldKind, kind);
        Assert.Equal("pending_approval", payload.RootElement.GetProperty("outcome").GetString());
        Assert.True((await log.VerifyAsync(ct: ct)).IsValid);
    }

    [Fact]
    public async Task Each_accepted_change_appends_one_row_in_order_and_the_chain_verifies()
    {
        var (ds, log, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, cancellationToken: ct);
        await store.SetAsync(HostSettings.ReadinessRequiredRoles, ["postgres", "search", "service_host", "cache"], _operator, cancellationToken: ct);
        await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, cancellationToken: ct); // no-op: no row

        var rows = await RowsAsync(ds);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(PostgresSettingAuditSink.ChangedKind, r.Kind));
        Assert.Equal("host.readiness.required_roles", rows[1].Payload.RootElement.GetProperty("key").GetString());
        Assert.True((await log.VerifyAsync(ct: ct)).IsValid);
    }
}
