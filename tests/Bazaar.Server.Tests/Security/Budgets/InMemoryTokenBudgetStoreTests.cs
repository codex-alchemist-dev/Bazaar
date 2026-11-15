using Bazaar.Server.Security.Budgets;

namespace Bazaar.Server.Tests.Security.Budgets;

public class InMemoryTokenBudgetStoreTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Loads_only_the_spends_strictly_after_the_given_instant_oldest_first()
    {
        var store = new InMemoryTokenBudgetStore();
        await store.Append("agent-a", _start.AddHours(2), 30, TestContext.Current.CancellationToken);
        await store.Append("agent-a", _start, 10, TestContext.Current.CancellationToken);
        await store.Append("agent-a", _start.AddHours(1), 20, TestContext.Current.CancellationToken);

        var loaded = await store.LoadSince("agent-a", _start, TestContext.Current.CancellationToken);

        Assert.Equal([new TokenBudgetSpend(_start.AddHours(1), 20), new TokenBudgetSpend(_start.AddHours(2), 30)], loaded);
    }

    [Fact]
    public async Task Loads_nothing_for_an_agent_with_no_spends()
    {
        var store = new InMemoryTokenBudgetStore();
        await store.Append("agent-a", _start, 10, TestContext.Current.CancellationToken);

        var loaded = await store.LoadSince("agent-b", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Spends_are_isolated_per_agent()
    {
        var store = new InMemoryTokenBudgetStore();
        await store.Append("agent-a", _start, 10, TestContext.Current.CancellationToken);
        await store.Append("agent-b", _start, 99, TestContext.Current.CancellationToken);

        var loaded = await store.LoadSince("agent-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);

        Assert.Equal(new TokenBudgetSpend(_start, 10), Assert.Single(loaded));
    }

    [Fact]
    public async Task Prune_removes_spends_at_or_before_the_instant_and_keeps_later_ones_for_every_agent()
    {
        var store = new InMemoryTokenBudgetStore();
        await store.Append("agent-a", _start, 10, TestContext.Current.CancellationToken);
        await store.Append("agent-a", _start.AddDays(1), 20, TestContext.Current.CancellationToken);
        await store.Append("agent-a", _start.AddDays(2), 40, TestContext.Current.CancellationToken);
        await store.Append("agent-b", _start.AddDays(-1), 30, TestContext.Current.CancellationToken);

        await store.PruneOlderThan(_start.AddDays(1), TestContext.Current.CancellationToken);

        var agentA = await store.LoadSince("agent-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        var agentB = await store.LoadSince("agent-b", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        Assert.Equal(new TokenBudgetSpend(_start.AddDays(2), 40), Assert.Single(agentA));
        Assert.Empty(agentB);
    }

    [Fact]
    public async Task Concurrent_appends_are_all_kept()
    {
        var store = new InMemoryTokenBudgetStore();

        await Task.WhenAll(Enumerable.Range(0, 1000).Select(i =>
            store.Append("agent-a", _start.AddSeconds(i), 1, TestContext.Current.CancellationToken)));

        var loaded = await store.LoadSince("agent-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        Assert.Equal(1000, loaded.Count);
        Assert.Equal(1000, loaded.Sum(spend => spend.Tokens));
    }

    [Fact]
    public async Task Append_rejects_negative_tokens()
    {
        var store = new InMemoryTokenBudgetStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.Append("agent-a", _start, -1, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_agent_ids_are_rejected(string agentId)
    {
        var store = new InMemoryTokenBudgetStore();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.Append(agentId, _start, 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.LoadSince(agentId, _start, TestContext.Current.CancellationToken));
    }

    private static TokenBudgetOptions Limits(long hour, long day, long week) =>
        new() { HourlyTokens = hour, DailyTokens = day, WeeklyTokens = week };

    [Fact]
    public async Task Try_consume_records_a_spend_that_fits_and_reports_the_remaining_room()
    {
        var store = new InMemoryTokenBudgetStore();

        var decision = await store.TryConsume("agent-a", _start, 40, Limits(100, 1000, 5000), TestContext.Current.CancellationToken);

        Assert.True(decision.Allowed);
        Assert.Equal(60, decision.Remaining);
        Assert.Equal(40, Assert.Single(await store.LoadSince("agent-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken)).Tokens);
    }

    [Fact]
    public async Task Try_consume_denies_when_any_window_is_full_and_records_nothing()
    {
        var store = new InMemoryTokenBudgetStore();
        var limits = Limits(100, 1000, 5000);
        await store.TryConsume("agent-a", _start, 90, limits, TestContext.Current.CancellationToken);

        var denied = await store.TryConsume("agent-a", _start.AddMinutes(10), 20, limits, TestContext.Current.CancellationToken);

        Assert.False(denied.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, denied.Window);
        Assert.Equal(10, denied.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(50), denied.RetryAfter);
        Assert.Equal(90, Assert.Single(await store.LoadSince("agent-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken)).Tokens);
    }

    [Fact]
    public async Task Try_consume_never_fits_a_call_larger_than_the_window_limit()
    {
        var store = new InMemoryTokenBudgetStore();

        var denied = await store.TryConsume("agent-a", _start, 101, Limits(100, 1000, 5000), TestContext.Current.CancellationToken);

        Assert.False(denied.Allowed);
        Assert.Equal(TimeSpan.MaxValue, denied.RetryAfter);
    }

    [Fact]
    public async Task Try_consume_of_zero_tokens_is_checked_but_not_recorded()
    {
        var store = new InMemoryTokenBudgetStore();

        var decision = await store.TryConsume("agent-a", _start, 0, Limits(100, 1000, 5000), TestContext.Current.CancellationToken);

        Assert.True(decision.Allowed);
        Assert.Empty(await store.LoadSince("agent-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Try_consume_rejects_bad_arguments()
    {
        var store = new InMemoryTokenBudgetStore();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsume(" ", _start, 1, Limits(1, 1, 1), ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryConsume("agent-a", _start, -1, Limits(1, 1, 1), ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.TryConsume("agent-a", _start, 1, null!, ct));
    }

    [Fact]
    public async Task Parallel_try_consume_calls_never_admit_more_than_the_limit()
    {
        var store = new InMemoryTokenBudgetStore();
        var limits = Limits(1_000, 1_000_000, 1_000_000);
        var ct = TestContext.Current.CancellationToken;

        // 400 calls of 7 tokens race for a 1000-token hour: at most 142 (994 tokens) may be admitted.
        var decisions = await Task.WhenAll(Enumerable.Range(0, 400).Select(_ =>
            Task.Run(() => store.TryConsume("agent-a", _start, 7, limits, ct), ct)));

        var admitted = decisions.Count(decision => decision.Allowed) * 7L;
        var recorded = (await store.LoadSince("agent-a", DateTimeOffset.MinValue, ct)).Sum(spend => spend.Tokens);
        Assert.True(admitted <= 1_000, $"Admitted {admitted} tokens against a limit of 1000.");
        Assert.Equal(admitted, recorded);
        Assert.Equal(142 * 7L, admitted);
    }

    [Fact]
    public async Task Parallel_try_consume_calls_for_different_agents_do_not_share_a_budget()
    {
        var store = new InMemoryTokenBudgetStore();
        var limits = Limits(100, 1000, 5000);
        var ct = TestContext.Current.CancellationToken;

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => store.TryConsume($"agent-{i}", _start, 100, limits, ct), ct)));

        Assert.All(decisions, decision => Assert.True(decision.Allowed));
    }
}
