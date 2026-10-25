using System.Text.Json;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Settings;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Settings;

public sealed class SettingsDb : PgFixture, IAsyncLifetime
{
    public override ValueTask InitializeAsync() => PgSource.IsUnsupportedHost ? ValueTask.CompletedTask : base.InitializeAsync();
}

/// <summary>Setting store against a real PostgreSQL, after the embedded migrations (doc 03 testing, doc 04).</summary>
public sealed class NpgsqlSettingStoreTests(SettingsDb db) : IClassFixture<SettingsDb>
{
    private static readonly SettingActor _operator = new("operator:dom", SettingRole.Operator);
    private static readonly SettingActor _agentActor = new("agent:charles", SettingRole.Agent);
    private static readonly string[] _defaultRoles = ["postgres", "search", "service_host"];
    private static readonly string[] _rolesWithCache = ["postgres", "search", "service_host", "cache"];

    private static async Task<(NpgsqlDataSource Data, InMemorySettingAuditSink Audit, NpgsqlSettingStore Store)> FreshAsync(SettingsDb db)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using (var cmd = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
            await cmd.ExecuteNonQueryAsync(ct);

        var ds = db.DataSource;
        await new MigrationRunner(ds).MigrateAsync(new EmbeddedMigrationSource(typeof(NpgsqlSettingStore).Assembly), "linux", ct);
        var audit = new InMemorySettingAuditSink();
        return (ds, audit, new NpgsqlSettingStore(ds, audit));
    }

    private static async Task<string?> RawValueAsync(NpgsqlDataSource ds, string key)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM setting WHERE key = @key AND scope = 'install' AND scope_id = ''");
        cmd.Parameters.AddWithValue("key", key);
        return (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string?> UpdatedByAsync(NpgsqlDataSource ds, string key)
    {
        await using var cmd = ds.CreateCommand("SELECT updated_by FROM setting WHERE key = @key");
        cmd.Parameters.AddWithValue("key", key);
        return (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static SettingChangeAudit[] Accepted(InMemorySettingAuditSink audit) =>
        [.. audit.Entries.Where(e => e.Outcome == SettingWriteOutcome.Accepted)];

    private static SettingChangeAudit[] Rejected(InMemorySettingAuditSink audit) =>
        [.. audit.Entries.Where(e => e.Outcome == SettingWriteOutcome.Rejected)];

    [Fact]
    public async Task Migration_creates_the_setting_table_with_its_key()
    {
        var (ds, _, _) = await FreshAsync(db);
        await using var cmd = ds.CreateCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'setting' AND column_name IN ('key', 'value', 'scope', 'scope_id', 'updated_at', 'updated_by')");
        Assert.Equal(6L, await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Defaults_are_returned_when_unset()
    {
        var (_, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(20, await store.GetAsync(HostSettings.ShutdownTimeoutSeconds, cancellationToken: ct));
        Assert.Equal(StatusBind.Loopback, await store.GetAsync(HostSettings.StatusBindAddress, cancellationToken: ct));
        Assert.Equal(_defaultRoles, await store.GetAsync(HostSettings.ReadinessRequiredRoles, cancellationToken: ct));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Accepted_change_is_stored_and_audited_exactly_once()
    {
        var (ds, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Accepted, result.Outcome);
        Assert.Null(result.Reason);
        Assert.Equal(30, await store.GetAsync(HostSettings.ShutdownTimeoutSeconds, cancellationToken: ct));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("host.shutdown_timeout_seconds", entry.Key);
        Assert.Equal(SettingScope.Install, entry.Scope);
        Assert.Equal("", entry.ScopeId);
        Assert.Equal("operator:dom", entry.ActorId);
        Assert.Equal(SettingWriteOutcome.Accepted, entry.Outcome);
        Assert.Equal("20", entry.OldValueJson);
        Assert.Equal("30", entry.NewValueJson);
        Assert.Equal("operator:dom", await UpdatedByAsync(ds, "host.shutdown_timeout_seconds"));
    }

    [Fact]
    public async Task Stored_value_survives_a_new_store_instance()
    {
        var (ds, _, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        await store.SetAsync(HostSettings.ReadinessRequiredRoles, ["postgres", "search", "service_host", "cache"], _operator, cancellationToken: ct);

        var reopened = new NpgsqlSettingStore(ds, new InMemorySettingAuditSink());
        var roles = await reopened.GetAsync(HostSettings.ReadinessRequiredRoles, cancellationToken: ct);
        Assert.Equal(_rolesWithCache, roles);
        Assert.NotNull(await RawValueAsync(ds, "host.readiness.required_roles"));
    }

    [Fact]
    public async Task Loosening_is_rejected_leaves_the_value_unchanged_and_writes_no_accepted_entry()
    {
        var (ds, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ReadinessRequiredRoles, ["search", "service_host"], _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("postgres", result.Reason);
        Assert.Equal(_defaultRoles, await store.GetAsync(HostSettings.ReadinessRequiredRoles, cancellationToken: ct));
        Assert.Null(await RawValueAsync(ds, "host.readiness.required_roles"));
        Assert.Empty(Accepted(audit));

        var entry = Assert.Single(Rejected(audit));
        Assert.Equal("[\"search\",\"service_host\"]", entry.NewValueJson);
    }

    [Fact]
    public async Task Administrator_service_account_is_rejected_and_stored_value_is_unchanged()
    {
        var (_, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ServiceAccount, "Administrator", _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Equal("bazaar-service", await store.GetAsync(HostSettings.ServiceAccount, cancellationToken: ct));
        Assert.Empty(Accepted(audit));
        Assert.Single(Rejected(audit));
    }

    [Fact]
    public async Task Tightening_is_accepted()
    {
        var (_, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ReadinessRequiredRoles, ["postgres", "search", "service_host", "cache"], _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Accepted, result.Outcome);
        Assert.Single(Accepted(audit));
    }

    [Fact]
    public async Task Approval_required_change_is_held_not_applied()
    {
        var (ds, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var bind = await store.SetAsync(HostSettings.StatusBindAddress, StatusBind.Lan, _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.PendingApproval, bind.Outcome);
        Assert.Equal(ApprovalTier.Tier2, bind.ApprovalTier);
        Assert.Equal(StatusBind.Loopback, await store.GetAsync(HostSettings.StatusBindAddress, cancellationToken: ct));
        Assert.Null(await RawValueAsync(ds, "host.status.bind"));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SettingWriteOutcome.PendingApproval, entry.Outcome);
        Assert.Empty(Accepted(audit));
    }

    [Fact]
    public async Task Unknown_key_is_rejected_without_an_audit_entry()
    {
        var (_, audit, store) = await FreshAsync(db);
        using var doc = JsonDocument.Parse("1");

        var result = await store.SetJsonAsync("host.does_not_exist", doc.RootElement, _operator, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("not a registered setting", result.Reason);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Wrong_type_is_rejected_and_audited_as_rejected()
    {
        var (ds, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        using var doc = JsonDocument.Parse("\"thirty\"");

        var result = await store.SetJsonAsync("host.shutdown_timeout_seconds", doc.RootElement, _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("expects", result.Reason);
        Assert.Equal(20, await store.GetAsync(HostSettings.ShutdownTimeoutSeconds, cancellationToken: ct));
        Assert.Null(await RawValueAsync(ds, "host.shutdown_timeout_seconds"));
        Assert.Single(Rejected(audit));
    }

    [Fact]
    public async Task Actor_without_the_required_role_is_rejected()
    {
        var (_, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _agentActor, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Equal(20, await store.GetAsync(HostSettings.ShutdownTimeoutSeconds, cancellationToken: ct));
        Assert.Empty(Accepted(audit));
    }

    [Fact]
    public async Task Repeating_the_stored_value_is_a_no_op_with_no_new_entry()
    {
        var (_, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, cancellationToken: ct);
        var again = await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Accepted, again.Outcome);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task Project_scoped_write_to_an_install_key_is_rejected()
    {
        var (_, audit, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;

        var result = await store.SetAsync(HostSettings.ShutdownTimeoutSeconds, 30, _operator, scopeId: "proj-1", cancellationToken: ct);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("install setting", result.Reason);
        Assert.Equal(SettingWriteOutcome.Rejected, Assert.Single(audit.Entries).Outcome);
    }

    [Fact]
    public async Task Invalid_stored_value_fails_the_read_closed()
    {
        var (ds, _, store) = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        await using (var cmd = ds.CreateCommand(
            "INSERT INTO setting (key, scope, scope_id, value, updated_by) VALUES ('host.service_account', 'install', '', '\"Administrator\"', 'manual')"))
            await cmd.ExecuteNonQueryAsync(ct);

        var ex = await Assert.ThrowsAsync<InvalidSettingValueException>(() => store.GetAsync(HostSettings.ServiceAccount, cancellationToken: ct));
        Assert.Equal("host.service_account", ex.Key);
    }

    [Fact]
    public async Task Unregistered_typed_key_is_rejected()
    {
        var (_, audit, store) = await FreshAsync(db);
        var stray = new SettingKey<int>("host.stray", 1, SettingScope.Install, SettingRole.Operator, "Not in the registry.");

        var result = await store.SetAsync(stray, 2, _operator, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Empty(audit.Entries);
    }
}
