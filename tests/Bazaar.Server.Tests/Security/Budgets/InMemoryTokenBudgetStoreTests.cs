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
}
