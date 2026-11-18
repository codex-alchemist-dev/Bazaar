using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Retention.Tests;

/// <summary>A cloned database with every embedded migration applied (common/ then retention/), as the host would.</summary>
public sealed class RetentionDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _owner;
    private NpgsqlDataSource? _app;

    /// <summary>The test database owner (superuser); used to arrange and read back rows.</summary>
    public NpgsqlDataSource Owner => _owner ?? throw new InvalidOperationException("RetentionDb is not initialised.");

    /// <summary>A data source whose connections run as the runtime role <c>bazaar_app</c>, so a missing grant fails the test.</summary>
    public NpgsqlDataSource App => _app ?? throw new InvalidOperationException("RetentionDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _owner = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 4 }.ConnectionString);
        await PgRoles.EnsureNoLoginRoleAsync(_owner, "bazaar_app");
        await new MigrationRunner(_owner, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);

        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            conn => { using var cmd = new NpgsqlCommand("SET ROLE bazaar_app", conn); cmd.ExecuteNonQuery(); },
            async conn => { await using var cmd = new NpgsqlCommand("SET ROLE bazaar_app", conn); await cmd.ExecuteNonQueryAsync(); });
        _app = builder.Build();
    }
}

/// <summary>The gw_usage purge SQL on a real PostgreSQL, run as the runtime role. Not runnable without BAZAAR_TEST_PG or Docker.</summary>
public sealed class GatewayUsagePurgeDbTests(RetentionDb db) : IClassFixture<RetentionDb>
{
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GatewayUsageRetentionCategory Category() => new(db.App, new FakeSettings());

    [Fact]
    public async Task Runtime_role_has_select_insert_and_delete_on_gw_usage_but_not_update()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using var cmd = db.Owner.CreateCommand(
            "SELECT has_table_privilege('bazaar_app', 'gw_usage', 'SELECT'), has_table_privilege('bazaar_app', 'gw_usage', 'DELETE'), " +
            "has_table_privilege('bazaar_app', 'gw_usage', 'UPDATE'), has_table_privilege('bazaar_app', 'gw_usage', 'INSERT')");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }

    [Fact]
    public async Task Purge_removes_only_rows_older_than_the_cutoff_oldest_first_and_honours_the_batch_limit()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ResetAsync();
        var cutoff = _now - TimeSpan.FromDays(30);
        // Five old rows (oldest is 100 days back) and two newer than the cutoff.
        foreach (var daysAgo in new[] { 100, 90, 80, 70, 60 })
            await InsertAsync(_now - TimeSpan.FromDays(daysAgo));
        await InsertAsync(_now - TimeSpan.FromDays(10));
        await InsertAsync(_now - TimeSpan.FromDays(1));

        var first = await Category().PurgeBatchAsync(cutoff, 3, Ct);

        Assert.Equal(3, first);
        var left = await TimestampsAsync();
        Assert.Equal(4, left.Count);
        Assert.Equal(_now - TimeSpan.FromDays(70), left[0]);
        Assert.Equal(_now - TimeSpan.FromDays(60), left[1]);

        var second = await Category().PurgeBatchAsync(cutoff, 3, Ct);
        Assert.Equal(2, second);
        Assert.Equal([_now - TimeSpan.FromDays(10), _now - TimeSpan.FromDays(1)], await TimestampsAsync());

        // Idempotent: nothing older is left, and the newer rows stay.
        Assert.Equal(0, await Category().PurgeBatchAsync(cutoff, 3, Ct));
        Assert.Equal(2, (await TimestampsAsync()).Count);
    }

    [Fact]
    public async Task A_row_exactly_at_the_cutoff_is_kept()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ResetAsync();
        var cutoff = _now - TimeSpan.FromDays(30);
        await InsertAsync(cutoff);

        Assert.Equal(0, await Category().PurgeBatchAsync(cutoff, 10, Ct));
        Assert.Single(await TimestampsAsync());
    }

    private async Task ResetAsync()
    {
        await using var cmd = db.Owner.CreateCommand("TRUNCATE gw_usage");
        await cmd.ExecuteNonQueryAsync(Ct);
        await using var provider = db.Owner.CreateCommand(
            "INSERT INTO gw_provider (id, kind, adapter, base_url, auth_kind) VALUES ('retention-test', 'local', 'openai_chat', 'http://localhost', 'none') ON CONFLICT (id) DO NOTHING");
        await provider.ExecuteNonQueryAsync(Ct);
    }

    private async Task InsertAsync(DateTimeOffset ts)
    {
        await using var cmd = db.Owner.CreateCommand(
            "INSERT INTO gw_usage (ts, agent_id, provider_id, key_ref, model_id, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, estimated) " +
            "VALUES ($1, 'agent-1', 'retention-test', 'key-1', 'model-1', 'native', 1, 0, 0, 0, 1, 0, 1, 'success', false)");
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = ts.UtcDateTime });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<DateTimeOffset>> TimestampsAsync()
    {
        var result = new List<DateTimeOffset>();
        await using var cmd = db.Owner.CreateCommand("SELECT ts FROM gw_usage ORDER BY ts");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            result.Add(new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero));
        return result;
    }
}
