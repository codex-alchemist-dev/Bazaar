using Bazaar.Server.Security.Budgets;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.Budgets;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): a misbehaving agent or upstream reports absurd token counts, races many calls
/// against one remaining unit of budget, or the clock jumps. Admitted spend must never exceed a window's limit.
/// </summary>
public class TokenBudgetChaosTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TokenBudgetOptions Limits(long hour, long day, long week) =>
        new() { HourlyTokens = hour, DailyTokens = day, WeeklyTokens = week };

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue - 99)]
    public async Task A_huge_spend_after_a_small_one_is_denied_and_does_not_wrap_the_sum(long huge)
    {
        var store = new InMemoryTokenBudgetStore();
        using var service = new TokenBudgetService(Limits(100, 1000, 5000), store, new FakeTimeProvider(_start));

        Assert.True((await service.TryConsume("agent-a", 1, Ct)).Allowed);

        // used + tokens overflows a long; the check must still see "more than the limit".
        var decision = await service.TryConsume("agent-a", huge, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.True(decision.Remaining >= 0);
        var spends = await store.LoadSince("agent-a", _start.AddDays(-8), Ct);
        Assert.Equal(1, spends.Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task A_huge_spend_at_an_operator_limit_of_long_max_is_bounded_too()
    {
        var store = new InMemoryTokenBudgetStore();
        using var service = new TokenBudgetService(Limits(long.MaxValue, long.MaxValue, long.MaxValue), store, new FakeTimeProvider(_start));

        Assert.True((await service.TryConsume("agent-a", long.MaxValue, Ct)).Allowed);
        var second = await service.TryConsume("agent-a", 1, Ct);

        Assert.False(second.Allowed);
        Assert.Equal(0, second.Remaining);
    }

    [Fact]
    public async Task Negative_token_counts_are_refused()
    {
        using var service = new TokenBudgetService(Limits(100, 1000, 5000), new InMemoryTokenBudgetStore(), new FakeTimeProvider(_start));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TryConsume("agent-a", -50, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TryConsume("agent-a", long.MinValue, Ct));
    }

    [Fact]
    public async Task Many_concurrent_calls_against_one_remaining_token_admit_exactly_one()
    {
        for (var round = 0; round < 50; round++)
        {
            var store = new InMemoryTokenBudgetStore();
            var time = new FakeTimeProvider(_start);
            using var service = new TokenBudgetService(Limits(100, 1000, 5000), store, time);
            Assert.True((await service.TryConsume("agent-a", 99, Ct)).Allowed);

            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => service.TryConsume("agent-a", 1, Ct), Ct)));

            Assert.Equal(1, results.Count(result => result.Allowed));
            var spends = await store.LoadSince("agent-a", _start.AddDays(-8), Ct);
            Assert.Equal(100, spends.Sum(spend => spend.Tokens));
        }
    }

    [Fact]
    public async Task Two_services_over_one_store_still_admit_only_what_fits()
    {
        // Two hosts (or a restart racing the old process) share one durable store; the store's atomic check is the limit.
        var store = new InMemoryTokenBudgetStore();
        var time = new FakeTimeProvider(_start);
        using var first = new TokenBudgetService(Limits(10, 1000, 5000), store, time);
        using var second = new TokenBudgetService(Limits(10, 1000, 5000), store, time);

        var calls = Enumerable.Range(0, 40).Select(i => Task.Run(() => (i % 2 == 0 ? first : second).TryConsume("agent-a", 1, Ct), Ct));
        var results = await Task.WhenAll(calls);

        Assert.Equal(10, results.Count(result => result.Allowed));
    }

    [Fact]
    public async Task A_clock_that_steps_backwards_does_not_free_budget()
    {
        var store = new InMemoryTokenBudgetStore();
        var time = new SteppingClock { Now = _start.AddHours(5) };
        using var service = new TokenBudgetService(Limits(100, 1000, 5000), store, time);

        Assert.True((await service.TryConsume("agent-a", 100, Ct)).Allowed);

        // The host clock is corrected back by 30 minutes (NTP step). The spend now looks like it is in the future.
        time.Now = _start.AddHours(4.5);
        var afterStep = await service.TryConsume("agent-a", 1, Ct);

        Assert.False(afterStep.Allowed);
    }

    /// <summary>A clock that may step backwards, which <see cref="FakeTimeProvider"/> refuses to do.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
