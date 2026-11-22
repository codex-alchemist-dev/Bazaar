using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Gateway.Retention.Tests;

/// <summary>A database with the gateway tables (common/0002) and the retention tombstone (gateway/0001), run as <c>bazaar_app</c>.</summary>
public sealed class UsageDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _app;

    /// <summary>Connections run as the runtime role, so a missing grant fails the test. The owner source (<c>DataSource</c>) arranges rows.</summary>
    public NpgsqlDataSource App => _app ?? throw new InvalidOperationException("UsageDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        await PgRoles.EnsureNoLoginRoleAsync(DataSource, "bazaar_app");
        foreach (var resource in new[] { "0002_gateway.sql", "gateway_0001.sql" })
        {
            await using var stream = typeof(UsageDb).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"{resource} is not embedded in this test assembly.");
            using var reader = new StreamReader(stream);
            await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var provider = DataSource.CreateCommand(
            "INSERT INTO gw_provider (id, kind, adapter, base_url, auth_kind) VALUES ('test', 'local', 'openai_chat', 'http://localhost', 'none') ON CONFLICT DO NOTHING"))
        {
            await provider.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            conn => { using var cmd = new NpgsqlCommand("SET ROLE bazaar_app", conn); cmd.ExecuteNonQuery(); },
            async conn => { await using var cmd = new NpgsqlCommand("SET ROLE bazaar_app", conn); await cmd.ExecuteNonQueryAsync(); });
        _app = builder.Build();
    }
}

/// <summary>The gw_usage store SQL on a real PostgreSQL, as the runtime role. Skipped without BAZAAR_TEST_PG or Docker.</summary>
public sealed class GatewayUsageStoreDbTests(UsageDb db) : IClassFixture<UsageDb>
{
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GatewayUsageRetentionStore Store() => new(db.App);

    [Fact]
    public async Task Soft_delete_hides_a_row_and_purge_removes_only_tombstoned_rows()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ResetAsync();
        var old = await InsertAsync(_now.AddDays(-100));
        var fresh = await InsertAsync(_now.AddDays(-1));

        var candidates = await Store().ListSoftDeleteCandidatesAsync(_now.AddDays(-30), 10, Ct);
        Assert.Equal([old.ToString("D")], candidates.Select(c => c.Id));

        // Purging a live row is refused by the statement itself.
        await Store().PurgeAsync([old.ToString("D"), fresh.ToString("D")], Ct);
        Assert.Equal(2, await CountAsync());

        await Store().SoftDeleteAsync([old.ToString("D")], _now, Ct);
        Assert.Empty(await Store().ListSoftDeleteCandidatesAsync(_now.AddDays(-30), 10, Ct));
        var due = await Store().ListPurgeCandidatesAsync(_now.AddSeconds(1), 10, Ct);
        Assert.Equal(old.ToString("D"), Assert.Single(due).Id);
        Assert.Empty(await Store().ListPurgeCandidatesAsync(_now.AddSeconds(-1), 10, Ct));

        await Store().PurgeAsync([old.ToString("D"), fresh.ToString("D")], Ct);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Lists_are_oldest_first_and_bounded()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ResetAsync();
        foreach (var days in new[] { 60, 100, 80 })
            await InsertAsync(_now.AddDays(-days));

        var rows = await Store().ListSoftDeleteCandidatesAsync(_now, 2, Ct);

        Assert.Equal([_now.AddDays(-100), _now.AddDays(-80)], rows.Select(r => r.CreatedAt));
    }

    [Fact]
    public async Task Soft_delete_is_idempotent_and_keeps_the_first_tombstone()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ResetAsync();
        var id = await InsertAsync(_now.AddDays(-100));

        await Store().SoftDeleteAsync([id.ToString("D")], _now, Ct);
        await Store().SoftDeleteAsync([id.ToString("D")], _now.AddDays(5), Ct);

        var due = await Store().ListPurgeCandidatesAsync(_now.AddDays(1), 10, Ct);
        Assert.Equal(_now, Assert.Single(due).SoftDeletedAt);
    }

    [Fact]
    public async Task Runtime_role_may_update_only_the_tombstone_column()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ResetAsync();
        await InsertAsync(_now.AddDays(-100));

        await using var cmd = db.App.CreateCommand("UPDATE gw_usage SET input = 1");
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
    }

    private async Task ResetAsync()
    {
        await using var cmd = db.DataSource.CreateCommand("DELETE FROM gw_usage");
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<int> CountAsync()
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM gw_usage");
        return (int)(long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<Guid> InsertAsync(DateTimeOffset ts)
    {
        var id = Guid.NewGuid();
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gw_usage (id, ts, agent_id, provider_id, key_ref, model_id, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, estimated) " +
            "VALUES ($1, $2, 'agent', 'test', 'k', 'm', 'native', 0, 0, 0, 0, 0, 0, 0, 'success', false)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = ts.UtcDateTime });
        await cmd.ExecuteNonQueryAsync(Ct);
        return id;
    }
}
