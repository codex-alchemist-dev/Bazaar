using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Security.Budgets;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Server.AgentSafety.Tests.Budgets.BudgetTestAmounts;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign) for the durable budget store: a reported count near long.MaxValue, and many
/// services (hosts) racing one remaining token. Admitted spend must never exceed the limit.
/// </summary>
public sealed class PostgresTokenBudgetChaosTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static TokenBudgetOptions HourLimit(long hour) => new() { HourlyTokens = T(hour), DailyTokens = T(1000), WeeklyTokens = T(5000) };

    [Fact]
    public async Task A_long_max_spend_after_a_small_one_is_denied_and_not_written()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        Assert.True((await store.TryConsume("agent-a", _t0, 1, HourLimit(100), Ct)).Allowed);
        var decision = await store.TryConsume("agent-a", _t0.AddSeconds(1), long.MaxValue, HourLimit(100), Ct);

        Assert.False(decision.Allowed);
        var spends = await store.LoadSince("agent-a", _t0.AddDays(-8), Ct);
        Assert.Equal(T(1), spends.Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task Many_services_racing_one_remaining_token_admit_exactly_one()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var time = new FakeTimeProvider(_t0);
        Assert.True((await store.TryConsume("agent-a", _t0, 99, HourLimit(100), Ct)).Allowed);

        // Eight services stand in for eight host processes; each serialises only its own calls.
        var services = Enumerable.Range(0, 8).Select(_ => new TokenBudgetService(HourLimit(100), store, time)).ToArray();
        try
        {
            var calls = Enumerable.Range(0, 48).Select(i => Task.Run(() => services[i % services.Length].TryConsume("agent-a", 1, Ct), Ct));
            var results = await Task.WhenAll(calls);

            Assert.Equal(1, results.Count(result => result.Allowed));
            var spends = await store.LoadSince("agent-a", _t0.AddDays(-8), Ct);
            Assert.Equal(T(100), spends.Sum(spend => spend.Tokens));
        }
        finally
        {
            foreach (var service in services)
            {
                service.Dispose();
            }
        }
    }

    [Fact]
    public async Task Sixty_four_racers_for_one_remaining_token_admit_exactly_one()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        Assert.True((await store.TryConsume("agent-a", _t0, 99, HourLimit(100), Ct)).Allowed);

        var calls = Enumerable.Range(0, 64).Select(_ => Task.Run(() => store.TryConsume("agent-a", _t0.AddSeconds(1), 1, HourLimit(100), Ct), Ct));
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, results.Count(result => result.Allowed));
        Assert.Equal(T(100), (await store.LoadSince("agent-a", _t0.AddDays(-8), Ct)).Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task The_largest_numeric_amount_round_trips_through_the_store()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var bounds = TokenAmountBounds.PostgresNumeric;
        var largest = new string('9', bounds.MaxIntegerDigits) + "." + new string('9', bounds.MaxFractionDigits);
        var amount = T("1" + new string('0', bounds.MaxIntegerDigits - 1) + "." + new string('0', bounds.MaxFractionDigits - 1) + "7");

        var decision = await store.TryConsume("agent-a", _t0, amount, Limits(largest, largest, largest), Ct);

        Assert.True(decision.Allowed);
        var stored = Assert.Single(await store.LoadSince("agent-a", _t0.AddDays(-8), Ct)).Tokens;
        Assert.Equal(amount, stored);
        Assert.Equal(bounds.MaxIntegerDigits, stored.IntegerDigitCount);
        Assert.Equal(bounds.MaxFractionDigits, stored.FractionDigitCount);
    }

    [Fact]
    public async Task A_spend_of_ten_to_the_131071_is_denied_as_never_fitting()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        var decision = await store.TryConsume("agent-a", _t0, T("1e131071"), new TokenBudgetOptions(), Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Empty(await store.LoadSince("agent-a", _t0.AddDays(-8), Ct));
    }

    [Fact]
    public async Task Spends_that_sum_past_long_max_are_counted_exactly()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var limits = Limits("1e30", "1e30", "1e30");

        var first = await store.TryConsume("agent-a", _t0, T("6e29"), limits, Ct);
        var tooMuch = await store.TryConsume("agent-a", _t0.AddSeconds(1), T("5e29"), limits, Ct);
        var exact = await store.TryConsume("agent-a", _t0.AddSeconds(2), T("4e29"), limits, Ct);

        Assert.True(first.Allowed);
        Assert.False(tooMuch.Allowed);
        Assert.Equal(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1), tooMuch.RetryAfter);
        Assert.True(exact.Allowed);
        Assert.Equal(TokenAmount.Zero, exact.Remaining);
        Assert.Equal(T("1e30"), (await store.LoadSince("agent-a", _t0.AddDays(-8), Ct)).Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task Fractional_halves_fill_a_limit_of_one_exactly()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var limits = Limits("1", "10", "10");

        Assert.True((await store.TryConsume("agent-a", _t0, T("0.5"), limits, Ct)).Allowed);
        var second = await store.TryConsume("agent-a", _t0.AddSeconds(1), T("0.50"), limits, Ct);
        var smallest = await store.TryConsume("agent-a", _t0.AddSeconds(2), T("1e-16383"), limits, Ct);

        Assert.True(second.Allowed);
        Assert.Equal(TokenAmount.Zero, second.Remaining);
        Assert.False(smallest.Allowed);
        Assert.Equal(TokenAmount.One, (await store.LoadSince("agent-a", _t0.AddDays(-8), Ct)).Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task A_window_sum_beyond_numeric_is_denied_not_thrown()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var bounds = TokenAmountBounds.PostgresNumeric;
        var largest = new string('9', bounds.MaxIntegerDigits);

        // Append writes around the check (tooling), so two maximal rows can exist; their sum has no numeric form.
        await store.Append("agent-a", _t0, T(largest), Ct);
        await store.Append("agent-a", _t0.AddSeconds(1), T(largest), Ct);
        var decision = await store.TryConsume("agent-a", _t0.AddSeconds(2), T(1), Limits(largest, largest, largest), Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Equal(2, (await store.LoadSince("agent-a", _t0.AddDays(-8), Ct)).Count);

        // The connection went back to the pool clean: the next call works.
        Assert.Empty(await store.LoadSince("agent-b", _t0.AddDays(-8), Ct));
    }

    [Fact]
    public async Task An_amount_beyond_numeric_is_denied_when_the_limit_allows_it_and_never_truncated()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var bounds = TokenAmountBounds.PostgresNumeric;
        var huge = T(new string('9', bounds.MaxIntegerDigits)) + TokenAmount.One;
        var limits = new TokenBudgetOptions { HourlyTokens = huge + huge, DailyTokens = huge + huge, WeeklyTokens = huge + huge };

        var decision = await store.TryConsume("agent-a", _t0, huge, limits, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Empty(await store.LoadSince("agent-a", _t0.AddDays(-8), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.Append("agent-a", _t0, huge, Ct));
    }
}
