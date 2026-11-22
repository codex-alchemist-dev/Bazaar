using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Gateway.Data;

/// <summary>
/// Real-Postgres tests for the exact token counts of <c>gw_usage</c> (doc 48, DECIDED 11.3; migration <c>gateway/0001</c>):
/// counts past the 64-bit range round trip, sums stay exact, a count outside the numeric limits is refused without writing
/// anything, and the migration keeps the values of rows that were already <c>bigint</c>.
/// </summary>
public sealed class UsageNumericDataTests(GatewayDb db) : IClassFixture<GatewayDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static GatewayUsageRecord Row(string agent, string provider, GatewayTokenUsage tokens) => new(
        Guid.NewGuid(), _t0, agent, "turn-1", provider, "key-1", "claude-haiku-5-5", null, GatewayIdentity.Native,
        tokens, 250, UsageStatus.Success, null, null, false, null);

    private async Task<UsageRepository> RepositoryWithProviderAsync(string provider)
    {
        await new GatewayDirectoryRepository(db.DataSource).UpsertProviderAsync(
            new GwProvider(provider, "first_party", "openai_chat", "https://example.invalid/v1", "api_key", "secret://bazaar/providers/x/key", "{}", null, null, true), Ct);
        return new UsageRepository(db.DataSource);
    }

    [Fact]
    public async Task Counts_past_the_long_range_and_fractions_round_trip_exactly()
    {
        SkipWithoutDatabase();
        var repo = await RepositoryWithProviderAsync("p-numeric");
        var huge = TokenAmount.Parse("123456789012345678901234567890123456789");
        var tokens = new GatewayTokenUsage(
            Input: huge,
            CacheWrite5m: TokenAmount.Parse("0.25"),
            CacheWrite1h: TokenAmount.FromInt64(long.MaxValue),
            CacheRead: TokenAmount.Parse(new string('9', 5_000)),
            Output: TokenAmount.Parse("98765432109876543210.5"),
            Thinking: TokenAmount.One);

        await repo.AppendAsync(Row("agent-numeric", "p-numeric", tokens), Ct);

        var read = Assert.Single(await repo.ListAsync("agent-numeric", _t0, _t0.AddHours(1), Ct));
        Assert.Equal(tokens, read.Tokens);
    }

    [Fact]
    public async Task Sums_of_counts_past_the_long_range_are_exact_in_the_database()
    {
        SkipWithoutDatabase();
        var repo = await RepositoryWithProviderAsync("p-numeric-sum");
        var tokens = new GatewayTokenUsage(long.MaxValue, 0, 0, 0, long.MaxValue, 0);
        for (var i = 0; i < 3; i++)
        {
            await repo.AppendAsync(Row("agent-numeric-sum", "p-numeric-sum", tokens), Ct);
        }

        // A bigint SUM would raise an out-of-range error here; numeric keeps all 20 digits.
        await using var cmd = db.DataSource.CreateCommand("SELECT SUM(input)::text, SUM(output)::text FROM gw_usage WHERE agent_id = 'agent-numeric-sum'");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal("27670116110564327421", reader.GetString(0));
        Assert.Equal("27670116110564327421", reader.GetString(1));

        // The same sum through the repository's rows and the in-memory rollup agrees.
        var rows = await repo.ListAsync("agent-numeric-sum", _t0, _t0.AddHours(1), Ct);
        var hour = Assert.Single(UsageRollup.Build(rows, _t0, _t0.AddHours(1)));
        Assert.Equal(TokenAmount.Parse("27670116110564327421"), hour.Tokens.Input);
    }

    [Fact]
    public async Task A_count_outside_the_numeric_limits_is_refused_and_nothing_is_written()
    {
        SkipWithoutDatabase();
        var repo = await RepositoryWithProviderAsync("p-numeric-big");
        var wide = new TokenAmountBounds(200_000, 0);
        var tooBig = TokenAmount.Parse(new string('9', 131_073), wide);
        var row = Row("agent-numeric-big", "p-numeric-big", new GatewayTokenUsage(1, 0, 0, 0, 2, 0) with { CacheRead = tooBig });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repo.AppendAsync(row, Ct).AsTask());

        Assert.Empty(await repo.ListAsync("agent-numeric-big", _t0, _t0.AddHours(1), Ct));
    }

    [Fact]
    public async Task Migration_gateway_0001_turns_bigint_rows_into_numeric_and_keeps_every_value_and_check()
    {
        SkipWithoutDatabase();
        var source = await PgSource.GetSharedAsync();
        var name = $"gwusage_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(source.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(source.ConnectionStringFor(name));
            var all = new EmbeddedMigrationSource(typeof(AssemblyMarker).Assembly);

            // Only the frozen common namespace first: gw_usage is still bigint, as it was before this change.
            await new MigrationRunner(dataSource).MigrateAsync(new InMemoryMigrationSource(all.Load(MigrationNamespaces.Common)), "linux", Ct);
            await using (var provider = dataSource.CreateCommand(
                "INSERT INTO gw_provider (id, kind, adapter, base_url, auth_kind, secret_ref) " +
                "VALUES ('p-mig', 'first_party', 'openai_chat', 'https://example.invalid/v1', 'api_key', 'secret://bazaar/providers/x/key')"))
            {
                await provider.ExecuteNonQueryAsync(Ct);
            }

            Assert.Equal("bigint", await ColumnTypeAsync(dataSource, "input"));
            await using (var insert = dataSource.CreateCommand(
                "INSERT INTO gw_usage (ts, agent_id, provider_id, key_ref, model_id, identity, input, cache_write_5m, cache_write_1h, cache_read, output, thinking, latency_ms, status, estimated) " +
                "VALUES (now(), 'agent-mig', 'p-mig', 'k', 'm', 'native', 9223372036854775807, 1, 2, 3, 9223372036854775806, 5, 10, 'success', false)"))
            {
                await insert.ExecuteNonQueryAsync(Ct);
            }

            var result = await new MigrationRunner(dataSource).MigrateAsync(new InMemoryMigrationSource([.. all.Load(MigrationNamespaces.Common), .. all.Load("gateway")]), "linux", Ct);
            Assert.Contains(result.Applied, a => a.Namespace == "gateway" && a.Version == 1);

            foreach (var column in new[] { "input", "cache_write_5m", "cache_write_1h", "cache_read", "output", "thinking" })
            {
                Assert.Equal("numeric", await ColumnTypeAsync(dataSource, column));
            }

            await using (var values = dataSource.CreateCommand("SELECT input::text, cache_write_5m::text, cache_write_1h::text, cache_read::text, output::text, thinking::text FROM gw_usage WHERE agent_id = 'agent-mig'"))
            await using (var reader = await values.ExecuteReaderAsync(Ct))
            {
                Assert.True(await reader.ReadAsync(Ct));
                Assert.Equal(
                    ["9223372036854775807", "1", "2", "3", "9223372036854775806", "5"],
                    Enumerable.Range(0, 6).Select(reader.GetString).ToArray());
            }

            // The CHECK constraints from common/0002 survive the type change.
            await using var negative = dataSource.CreateCommand("UPDATE gw_usage SET cache_read = -1 WHERE agent_id = 'agent-mig'");
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => negative.ExecuteNonQueryAsync(Ct))).SqlState);
            await using var thinking = dataSource.CreateCommand("UPDATE gw_usage SET thinking = output + 1 WHERE agent_id = 'agent-mig'");
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => thinking.ExecuteNonQueryAsync(Ct))).SqlState);

            // Past the old bigint range now works.
            await using var past = dataSource.CreateCommand("UPDATE gw_usage SET input = input + 1, output = output + 10 WHERE agent_id = 'agent-mig'");
            Assert.Equal(1, await past.ExecuteNonQueryAsync(Ct));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(source.AdminConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task<string> ColumnTypeAsync(NpgsqlDataSource dataSource, string column)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT data_type FROM information_schema.columns WHERE table_name = 'gw_usage' AND column_name = $1");
        cmd.Parameters.AddWithValue(column);
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
