using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Budgets.Postgres;

/// <summary>
/// PostgresTokenBudgetStore.TryConsume against a real PostgreSQL: the advisory-locked check-and-insert must never admit
/// more than a window allows, even from several independent connection pools standing in for separate processes (doc 15).
/// </summary>
public sealed class PostgresTokenBudgetAtomicTests
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TokenBudgetOptions Limits(long hour, long day, long week) =>
        new() { HourlyTokens = hour, DailyTokens = day, WeeklyTokens = week };

    /// <summary>Most TryConsume calls in flight at once: each holds a connection while it waits on the agent's advisory lock.</summary>
    private const int _maxInFlight = 8;

    /// <summary>Caps the pool so a test cannot open more connections than the shared test server allows.</summary>
    private static NpgsqlDataSource PooledSource(string connectionString) =>
        NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 10 }.ConnectionString);

    [Fact]
    public async Task Try_consume_records_a_fitting_spend_and_reports_what_is_left()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        var decision = await store.TryConsume("agent-a", _t0, 40, Limits(100, 1000, 5000), Ct);

        Assert.True(decision.Allowed);
        Assert.Equal(60, decision.Remaining);
        Assert.Equal(40, Assert.Single(await store.LoadSince("agent-a", DateTimeOffset.MinValue, Ct)).Tokens);
    }

    [Fact]
    public async Task A_denied_try_consume_writes_nothing_and_names_the_blocking_window_and_retry_time()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var limits = Limits(100, 1000, 5000);
        await store.TryConsume("agent-a", _t0, 90, limits, Ct);

        var denied = await store.TryConsume("agent-a", _t0.AddMinutes(10), 20, limits, Ct);

        Assert.False(denied.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, denied.Window);
        Assert.Equal(10, denied.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(50), denied.RetryAfter);
        Assert.Equal(90, Assert.Single(await store.LoadSince("agent-a", DateTimeOffset.MinValue, Ct)).Tokens);
    }

    [Fact]
    public async Task Try_consume_checks_the_day_and_week_windows_too()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var limits = Limits(1000, 150, 5000);
        await store.TryConsume("agent-a", _t0, 100, limits, Ct);

        var denied = await store.TryConsume("agent-a", _t0.AddHours(3), 60, limits, Ct);

        Assert.False(denied.Allowed);
        Assert.Equal(TokenBudgetWindow.Day, denied.Window);
    }

    [Fact]
    public async Task Try_consume_of_zero_tokens_is_checked_but_not_recorded()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        var decision = await store.TryConsume("agent-a", _t0, 0, Limits(100, 1000, 5000), Ct);

        Assert.True(decision.Allowed);
        Assert.Empty(await store.LoadSince("agent-a", DateTimeOffset.MinValue, Ct));
    }

    [Fact]
    public async Task Parallel_try_consume_calls_on_one_pool_never_overspend()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        await using var pooled = PooledSource(db.ConnectionString);
        var store = new PostgresTokenBudgetStore(pooled);

        var admitted = await HammerAsync([store], calls: 120, tokensPerCall: 7, limits: Limits(500, 1_000_000, 1_000_000));

        // 120 x 7 = 840 wanted against 500: 71 calls (497 tokens) fit.
        Assert.Equal(71 * 7L, admitted);
        Assert.Equal(admitted, await RecordedAsync(store));
    }

    [Fact]
    public async Task Parallel_try_consume_calls_from_separate_pools_never_overspend()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);

        // Each data source has its own connection pool, standing in for a separate server process on one database.
        await using var firstSource = PooledSource(db.ConnectionString);
        await using var secondSource = PooledSource(db.ConnectionString);
        await using var thirdSource = PooledSource(db.ConnectionString);
        var stores = new[]
        {
            new PostgresTokenBudgetStore(firstSource),
            new PostgresTokenBudgetStore(secondSource),
            new PostgresTokenBudgetStore(thirdSource),
        };

        var admitted = await HammerAsync(stores, calls: 150, tokensPerCall: 7, limits: Limits(500, 1_000_000, 1_000_000));

        Assert.True(admitted <= 500, $"Admitted {admitted} tokens against a limit of 500.");
        Assert.Equal(71 * 7L, admitted);
        Assert.Equal(admitted, await RecordedAsync(stores[0]));
    }

    [Fact]
    public async Task Parallel_try_consume_calls_for_different_agents_each_get_their_own_budget()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var limits = Limits(100, 1000, 5000);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => store.TryConsume($"agent-{i}", _t0, 100, limits, Ct), Ct)));

        Assert.All(decisions, decision => Assert.True(decision.Allowed));
    }

    [Fact]
    public async Task Try_consume_rejects_bad_arguments_before_touching_the_database()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var store = new PostgresTokenBudgetStore(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsume(" ", _t0, 1, Limits(1, 1, 1), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryConsume("agent-a", _t0, -1, Limits(1, 1, 1), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.TryConsume("agent-a", _t0, 1, null!, Ct));
    }

    /// <summary>Fires <paramref name="calls"/> parallel spends for one agent round-robin over the stores; returns tokens admitted.</summary>
    private static async Task<long> HammerAsync(PostgresTokenBudgetStore[] stores, int calls, long tokensPerCall, TokenBudgetOptions limits)
    {
        using var gate = new SemaphoreSlim(_maxInFlight);
        var decisions = await Task.WhenAll(Enumerable.Range(0, calls).Select(i =>
            Task.Run(async () =>
            {
                await gate.WaitAsync(Ct);
                try
                {
                    return await stores[i % stores.Length].TryConsume("agent-a", _t0, tokensPerCall, limits, Ct);
                }
                finally
                {
                    gate.Release();
                }
            }, Ct)));
        return decisions.Count(decision => decision.Allowed) * tokensPerCall;
    }

    private static async Task<long> RecordedAsync(PostgresTokenBudgetStore store) =>
        (await store.LoadSince("agent-a", DateTimeOffset.MinValue, Ct)).Sum(spend => spend.Tokens);
}
