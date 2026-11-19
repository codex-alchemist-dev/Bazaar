using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.Budgets.Postgres;

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

    private static TokenBudgetOptions Limits(long hour) => new() { HourlyTokens = hour, DailyTokens = 1000, WeeklyTokens = 5000 };

    [Fact]
    public async Task A_long_max_spend_after_a_small_one_is_denied_and_not_written()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);

        Assert.True((await store.TryConsume("agent-a", _t0, 1, Limits(100), Ct)).Allowed);
        var decision = await store.TryConsume("agent-a", _t0.AddSeconds(1), long.MaxValue, Limits(100), Ct);

        Assert.False(decision.Allowed);
        var spends = await store.LoadSince("agent-a", _t0.AddDays(-8), Ct);
        Assert.Equal(1, spends.Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task Many_services_racing_one_remaining_token_admit_exactly_one()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresTokenBudgetStore(db.DataSource);
        var time = new FakeTimeProvider(_t0);
        Assert.True((await store.TryConsume("agent-a", _t0, 99, Limits(100), Ct)).Allowed);

        // Eight services stand in for eight host processes; each serialises only its own calls.
        var services = Enumerable.Range(0, 8).Select(_ => new TokenBudgetService(Limits(100), store, time)).ToArray();
        try
        {
            var calls = Enumerable.Range(0, 48).Select(i => Task.Run(() => services[i % services.Length].TryConsume("agent-a", 1, Ct), Ct));
            var results = await Task.WhenAll(calls);

            Assert.Equal(1, results.Count(result => result.Allowed));
            var spends = await store.LoadSince("agent-a", _t0.AddDays(-8), Ct);
            Assert.Equal(100, spends.Sum(spend => spend.Tokens));
        }
        finally
        {
            foreach (var service in services)
            {
                service.Dispose();
            }
        }
    }
}
