using Bazaar.Contracts.Numerics;
using Bazaar.Server.Security.Budgets;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Server.Tests.Security.Budgets.BudgetTestAmounts;

namespace Bazaar.Server.Tests.Security.Budgets;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): a misbehaving agent or upstream reports absurd token counts, races many calls
/// against one remaining unit of budget, or the clock jumps. Admitted spend must never exceed a window's limit.
/// </summary>
public class TokenBudgetChaosTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue - 99)]
    public async Task A_huge_spend_after_a_small_one_is_denied_and_does_not_wrap_the_sum(long huge)
    {
        var store = new InMemoryTokenBudgetStore();
        using var service = new TokenBudgetService(Limits(100, 1000, 5000), store, new FakeTimeProvider(_start));

        Assert.True((await service.TryConsume("agent-a", 1, Ct)).Allowed);

        // Once used + tokens wrapped a long. Amounts are exact now; the check must still see "more than the limit".
        var decision = await service.TryConsume("agent-a", huge, Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        var spends = await store.LoadSince("agent-a", _start.AddDays(-8), Ct);
        Assert.Equal(T(1), spends.Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task A_huge_spend_at_an_operator_limit_of_long_max_is_bounded_too()
    {
        var store = new InMemoryTokenBudgetStore();
        using var service = new TokenBudgetService(Limits(long.MaxValue, long.MaxValue, long.MaxValue), store, new FakeTimeProvider(_start));

        Assert.True((await service.TryConsume("agent-a", long.MaxValue, Ct)).Allowed);
        var second = await service.TryConsume("agent-a", 1, Ct);

        Assert.False(second.Allowed);
        Assert.Equal(TokenAmount.Zero, second.Remaining);
    }

    [Fact]
    public void Negative_token_counts_cannot_be_expressed()
    {
        // TryConsume takes a TokenAmount, which is never negative: the refusal moved from the service to the type.
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenAmount.FromInt64(-50));
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenAmount.FromInt64(long.MinValue));
        Assert.False(TokenAmount.TryParse("-50", out _));
    }

    [Fact]
    public async Task A_spend_of_ten_to_the_131071_is_denied_as_never_fitting_against_the_defaults()
    {
        var store = new InMemoryTokenBudgetStore();
        using var service = new TokenBudgetService(new TokenBudgetOptions(), store, new FakeTimeProvider(_start));

        var decision = await service.TryConsume("agent-a", T("1e131071"), Ct);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Empty(await store.LoadSince("agent-a", _start.AddDays(-8), Ct));
    }

    [Fact]
    public async Task Spends_that_sum_past_long_max_are_counted_exactly()
    {
        var store = new InMemoryTokenBudgetStore();
        using var service = new TokenBudgetService(Limits("1e30", "1e30", "1e30"), store, new FakeTimeProvider(_start));

        var first = await service.TryConsume("agent-a", T("6e29"), Ct);
        var tooMuch = await service.TryConsume("agent-a", T("5e29"), Ct);
        var exact = await service.TryConsume("agent-a", T("4e29"), Ct);

        Assert.True(first.Allowed);
        Assert.Equal(T("4e29"), first.Remaining);
        Assert.False(tooMuch.Allowed);
        Assert.Equal(T("4e29"), tooMuch.Remaining);
        Assert.True(exact.Allowed);
        Assert.Equal(TokenAmount.Zero, exact.Remaining);
        Assert.Equal(T("1e30"), (await store.LoadSince("agent-a", _start.AddDays(-8), Ct)).Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task The_retry_time_walk_counts_past_long_max_exactly()
    {
        var store = new InMemoryTokenBudgetStore();
        var time = new FakeTimeProvider(_start);
        using var service = new TokenBudgetService(Limits("1e30", "1e31", "1e31"), store, time);
        Assert.True((await service.TryConsume("agent-a", T("6e29"), Ct)).Allowed);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True((await service.TryConsume("agent-a", T("4e29"), Ct)).Allowed);

        // Needs 1e29 free: the first spend (6e29, past long.MaxValue on its own) leaves at 01:00.
        var denied = await service.TryConsume("agent-a", T("1e29"), Ct);

        Assert.False(denied.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(50), denied.RetryAfter);
    }

    [Fact]
    public async Task Fractional_halves_fill_a_limit_of_one_exactly()
    {
        var store = new InMemoryTokenBudgetStore();
        using var service = new TokenBudgetService(Limits("1", "10", "10"), store, new FakeTimeProvider(_start));

        var first = await service.TryConsume("agent-a", T("0.5"), Ct);
        var second = await service.TryConsume("agent-a", T("0.5"), Ct);
        var smallest = await service.TryConsume("agent-a", T("1e-16383"), Ct);

        Assert.True(first.Allowed);
        Assert.Equal(T("0.5"), first.Remaining);
        Assert.True(second.Allowed);
        Assert.Equal(TokenAmount.Zero, second.Remaining);
        Assert.False(smallest.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, smallest.Window);
        Assert.Equal(TokenAmount.One, (await store.LoadSince("agent-a", _start.AddDays(-8), Ct)).Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task An_amount_with_more_integer_digits_than_a_limit_is_denied_without_counting()
    {
        var store = new InMemoryTokenBudgetStore();

        var decision = await store.TryConsume("agent-a", _start, T("12345"), Limits(100, 1_000_000, 1_000_000), Ct);

        // The fast path compares digit counts only. It reports zero remaining because it counted nothing; the full check
        // would have reported the hour's 100.
        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
        Assert.Equal(TokenAmount.Zero, decision.Remaining);

        var sameDigits = await store.TryConsume("agent-a", _start, T(101), Limits(100, 1_000_000, 1_000_000), Ct);
        Assert.Equal(T(100), sameDigits.Remaining);
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
            Assert.Equal(T(100), spends.Sum(spend => spend.Tokens));
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
