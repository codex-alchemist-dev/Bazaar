using Bazaar.Contracts.Numerics;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Bazaar.Server.Tests.Security.Budgets.BudgetTestAmounts;

namespace Bazaar.Server.Tests.Security.Budgets.Postgres;

/// <summary>PostgresTokenBudgetStore against a real PostgreSQL: append, ordered reads, pruning, isolation and recovery (doc 15).</summary>
public sealed class PostgresTokenBudgetStoreTests
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Append_then_load_since_returns_the_spend()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        await store.Append("agent-a", _t0.AddMinutes(10), 120, Ct);
        var spends = await store.LoadSince("agent-a", _t0, Ct);

        var spend = Assert.Single(spends);
        Assert.Equal(_t0.AddMinutes(10), spend.At);
        Assert.Equal(T(120), spend.Tokens);
    }

    [Fact]
    public async Task Load_since_excludes_the_boundary_instant()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        await store.Append("agent-a", _t0, 10, Ct);
        await store.Append("agent-a", _t0.AddSeconds(1), 20, Ct);

        var spends = await store.LoadSince("agent-a", _t0, Ct);

        var spend = Assert.Single(spends);
        Assert.Equal(T(20), spend.Tokens);
    }

    [Fact]
    public async Task Load_since_returns_oldest_first_even_when_appended_out_of_order()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        await store.Append("agent-a", _t0.AddMinutes(30), 3, Ct);
        await store.Append("agent-a", _t0.AddMinutes(10), 1, Ct);
        await store.Append("agent-a", _t0.AddMinutes(20), 2, Ct);

        var spends = await store.LoadSince("agent-a", _t0, Ct);

        Assert.Equal([T(1), T(2), T(3)], spends.Select(spend => spend.Tokens).ToArray());
    }

    [Fact]
    public async Task Prune_removes_spends_at_or_before_the_instant_and_keeps_later_ones()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        await store.Append("agent-a", _t0.AddMinutes(-5), 1, Ct);
        await store.Append("agent-a", _t0, 2, Ct);
        await store.Append("agent-a", _t0.AddMinutes(5), 3, Ct);

        await store.PruneOlderThan(_t0, Ct);

        var spends = await store.LoadSince("agent-a", _t0.AddDays(-1), Ct);
        Assert.Equal([T(3)], spends.Select(spend => spend.Tokens).ToArray());
    }

    [Fact]
    public async Task Spends_are_isolated_per_agent_for_loads_and_prunes()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        await store.Append("agent-a", _t0.AddMinutes(-5), 1, Ct);
        await store.Append("agent-b", _t0.AddMinutes(5), 7, Ct);

        await store.PruneOlderThan(_t0, Ct);

        Assert.Empty(await store.LoadSince("agent-a", _t0.AddDays(-1), Ct));
        var remaining = Assert.Single(await store.LoadSince("agent-b", _t0.AddDays(-1), Ct));
        Assert.Equal(T(7), remaining.Tokens);
    }

    [Fact]
    public async Task A_zero_token_append_records_nothing()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        await store.Append("agent-a", _t0, 0, Ct);

        Assert.Empty(await store.LoadSince("agent-a", _t0.AddDays(-1), Ct));
    }

    [Fact]
    public async Task Service_over_postgres_recovers_its_windows_in_a_new_instance()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var time = new FakeTimeProvider(_t0);

        using (var first = new TokenBudgetService(Limits(100, 1000, 5000), store, time))
        {
            var allowed = await first.TryConsume("agent-a", 90, Ct);
            Assert.True(allowed.Allowed);
        }

        // A new process over the same database sees the spend and must refuse the call that would pass the hour limit.
        using var second = new TokenBudgetService(Limits(100, 1000, 5000), store, time);
        var denied = await second.TryConsume("agent-a", 20, Ct);
        Assert.False(denied.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, denied.Window);

        var fits = await second.TryConsume("agent-a", 10, Ct);
        Assert.True(fits.Allowed);
    }

    [Fact]
    public async Task Migration_0005_makes_tokens_unconstrained_numeric_and_keeps_the_positive_check()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);

        await using (var typeCmd = db.DataSource.CreateCommand(
            "SELECT data_type, numeric_precision, numeric_scale FROM information_schema.columns " +
            "WHERE table_name = 'agent_token_spend' AND column_name = 'tokens'"))
        {
            await using var reader = await typeCmd.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal("numeric", reader.GetString(0));
            Assert.True(reader.IsDBNull(1), "tokens must have no precision limit.");
            Assert.True(reader.IsDBNull(2), "tokens must have no scale limit.");
        }

        foreach (var bad in new[] { "0", "-1", "0.000" })
        {
            await using var insert = db.DataSource.CreateCommand($"INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ('agent-a', now(), {bad})");
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await using var fraction = db.DataSource.CreateCommand("INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ('agent-a', now(), 0.25)");
        Assert.Equal(1, await fraction.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task Append_rejects_a_blank_agent_id_before_touching_the_database()
    {
        // Argument checks run before any command is built, so an unopened data source is enough here.
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var store = new PostgresTokenBudgetStore(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => store.Append(" ", _t0, 1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.Append("agent-a", _t0, -1, Ct));
    }
}
