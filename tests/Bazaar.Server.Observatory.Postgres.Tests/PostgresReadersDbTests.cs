using Bazaar.Contracts.Gateway;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Observatory.Postgres.Tests;

/// <summary>A database with the gateway tables (common/0002).</summary>
public sealed class GatewayDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            return;
        }

        await base.InitializeAsync();
        await using var stream = typeof(GatewayDb).Assembly.GetManifestResourceStream("0002_gateway.sql")
            ?? throw new InvalidOperationException("0002_gateway.sql is not embedded.");
        using var reader = new StreamReader(stream);
        await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
        await cmd.ExecuteNonQueryAsync();
        await using var provider = DataSource.CreateCommand(
            "INSERT INTO gw_provider (id, kind, adapter, base_url, auth_kind) VALUES ('test', 'local', 'openai_chat', 'http://localhost', 'none') ON CONFLICT DO NOTHING");
        await provider.ExecuteNonQueryAsync();
    }
}

/// <summary>The readers on a real PostgreSQL. Skipped without BAZAAR_TEST_PG or Docker.</summary>
public sealed class PostgresReadersDbTests(GatewayDb db) : IClassFixture<GatewayDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Range_and_turn_reads_return_rows_with_every_column()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ExecAsync("DELETE FROM gw_usage");
        await InsertAsync("a1", "t1", _t0, "native");
        await InsertAsync("a2", "t2", _t0.AddHours(5), "pooled");
        var reader = new PostgresUsageReader(db.DataSource);

        var range = await reader.ReadRangeAsync(_t0.AddHours(-1), _t0.AddHours(1), Ct);
        var turn = await reader.ReadTurnAsync("t2", Ct);

        var row = Assert.Single(range);
        Assert.Equal(("a1", GatewayIdentity.Native, 100L, 40L), (row.AgentId, row.Identity, row.Tokens.Input, row.Tokens.Output));
        Assert.Equal(GatewayIdentity.Pooled, Assert.Single(turn).Identity);
    }

    [Fact]
    public async Task Tombstoned_rows_are_hidden_once_the_retention_column_exists()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ExecAsync("DELETE FROM gw_usage");
        await InsertAsync("a1", "t1", _t0, "native");
        await ExecAsync("ALTER TABLE gw_usage ADD COLUMN IF NOT EXISTS soft_deleted_at timestamptz NULL");
        await ExecAsync("UPDATE gw_usage SET soft_deleted_at = now()");

        var rows = await new PostgresUsageReader(db.DataSource).ReadRangeAsync(_t0.AddHours(-1), _t0.AddHours(1), Ct);

        Assert.Empty(rows);
        await ExecAsync("ALTER TABLE gw_usage DROP COLUMN soft_deleted_at");
    }

    [Fact]
    public async Task Lanes_read_state_and_observed_figures()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await ExecAsync("DELETE FROM gw_quota_state");
        await ExecAsync("INSERT INTO gw_quota_state (provider_id, key_ref, model_class, state, observed) VALUES ('test', 'k', 'haiku', 'rate_limited', '{\"requests-remaining\": 3, \"requests-limit\": 50}')");

        var lane = Assert.Single(await new PostgresLaneReader(db.DataSource).ReadAsync(Ct));

        Assert.Equal((QuotaState.RateLimited, 3L, 50L), (lane.State, lane.RemainingRequests, lane.LimitRequests));
    }

    private async Task ExecAsync(string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task InsertAsync(string agent, string turn, DateTimeOffset ts, string identity)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gw_usage (ts, agent_id, turn_id, provider_id, key_ref, model_id, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, estimated) " +
            "VALUES ($1, $2, $3, 'test', 'k', 'm', $4, 100, 0, 0, 0, 40, 0, 5, 'success', false)");
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = ts.UtcDateTime });
        cmd.Parameters.AddWithValue(agent);
        cmd.Parameters.AddWithValue(turn);
        cmd.Parameters.AddWithValue(identity);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}
