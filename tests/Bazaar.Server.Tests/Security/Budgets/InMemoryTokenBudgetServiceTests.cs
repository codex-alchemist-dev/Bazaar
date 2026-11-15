using Bazaar.Server.Security.Budgets;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.Budgets;

public class InMemoryTokenBudgetServiceTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    private static TokenBudgetOptions Limits(long hour, long day, long week) =>
        new() { HourlyTokens = hour, DailyTokens = day, WeeklyTokens = week };

    private static Task<TokenBudgetDecision> Consume(InMemoryTokenBudgetService service, string agentId, long tokens) =>
        service.TryConsume(agentId, tokens, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Allows_spend_under_every_limit_and_reports_what_remains()
    {
        var service = new InMemoryTokenBudgetService(Limits(200, 1000, 5000), new FakeTimeProvider(_start));

        var decision = await Consume(service, "agent-a", 120);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Window);
        Assert.Equal(80, decision.Remaining);
        Assert.Equal(TimeSpan.Zero, decision.RetryAfter);
    }

    [Fact]
    public async Task Allows_a_spend_that_lands_exactly_on_the_limit()
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));

        var decision = await Consume(service, "agent-a", 100);

        Assert.True(decision.Allowed);
        Assert.Equal(0, decision.Remaining);
    }

    [Fact]
    public async Task Denies_when_the_hour_window_is_full()
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));
        await Consume(service, "agent-a", 90);

        var decision = await Consume(service, "agent-a", 20);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.Equal(10, decision.Remaining);
    }

    [Fact]
    public async Task Denies_when_the_day_window_is_full_even_though_the_hour_has_room()
    {
        var time = new FakeTimeProvider(_start);
        var service = new InMemoryTokenBudgetService(Limits(100, 150, 5000), time);
        await Consume(service, "agent-a", 90);
        time.Advance(TimeSpan.FromHours(2));

        var decision = await Consume(service, "agent-a", 90);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Day, decision.Window);
    }

    [Fact]
    public async Task Denies_when_the_week_window_is_full_even_though_hour_and_day_have_room()
    {
        var time = new FakeTimeProvider(_start);
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 150), time);
        await Consume(service, "agent-a", 90);
        time.Advance(TimeSpan.FromHours(25));

        var decision = await Consume(service, "agent-a", 90);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Week, decision.Window);
    }

    [Fact]
    public async Task A_denied_call_records_nothing()
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));
        await Consume(service, "agent-a", 90);
        var denied = await Consume(service, "agent-a", 20);

        var followUp = await Consume(service, "agent-a", 10);

        Assert.False(denied.Allowed);
        Assert.True(followUp.Allowed, "The denied 20 tokens must not have been counted.");
        Assert.Equal(0, followUp.Remaining);
    }

    [Fact]
    public async Task A_call_denied_by_the_day_window_does_not_count_against_the_hour_window()
    {
        var time = new FakeTimeProvider(_start);
        var service = new InMemoryTokenBudgetService(Limits(100, 150, 5000), time);
        await Consume(service, "agent-a", 90);
        time.Advance(TimeSpan.FromHours(2));
        var denied = await Consume(service, "agent-a", 90);

        var followUp = await Consume(service, "agent-a", 60);

        Assert.False(denied.Allowed);
        Assert.True(followUp.Allowed, "Only the first 90 tokens were counted, so 60 more fits the day limit of 150.");
    }

    [Fact]
    public async Task Hour_window_frees_tokens_once_the_spend_is_an_hour_old()
    {
        var time = new FakeTimeProvider(_start);
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), time);
        await Consume(service, "agent-a", 100);
        var blocked = await Consume(service, "agent-a", 1);

        time.Advance(TimeSpan.FromHours(1));
        var allowed = await Consume(service, "agent-a", 100);

        Assert.False(blocked.Allowed);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public async Task Retry_after_points_at_the_moment_enough_old_spend_leaves_the_window()
    {
        var time = new FakeTimeProvider(_start);
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), time);
        await Consume(service, "agent-a", 40);
        time.Advance(TimeSpan.FromMinutes(20));
        await Consume(service, "agent-a", 40);
        time.Advance(TimeSpan.FromMinutes(10));
        await Consume(service, "agent-a", 20);
        time.Advance(TimeSpan.FromMinutes(1));

        // Needs 10 free tokens. The first 40 leaves at 01:00, which frees enough room.
        var decision = await Consume(service, "agent-a", 10);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(29), decision.RetryAfter);
    }

    [Fact]
    public async Task A_call_larger_than_the_window_limit_never_fits()
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));

        var decision = await Consume(service, "agent-a", 101);

        Assert.False(decision.Allowed);
        Assert.Equal(TokenBudgetWindow.Hour, decision.Window);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
    }

    [Fact]
    public async Task Zero_tokens_is_always_allowed_while_under_every_limit()
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));

        var decision = await Consume(service, "agent-a", 0);

        Assert.True(decision.Allowed);
        Assert.Equal(100, decision.Remaining);
    }

    [Fact]
    public async Task Budgets_are_isolated_per_agent()
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));
        await Consume(service, "agent-a", 100);

        var other = await Consume(service, "agent-b", 100);
        var same = await Consume(service, "agent-a", 1);

        Assert.True(other.Allowed);
        Assert.False(same.Allowed);
    }

    [Fact]
    public async Task Concurrent_calls_never_exceed_the_limit()
    {
        const long limit = 1000;
        const long perCall = 7;
        var service = new InMemoryTokenBudgetService(Limits(limit, 100_000, 100_000), new FakeTimeProvider(_start));

        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            var allowed = 0;
            for (var i = 0; i < 100; i++)
            {
                if ((await Consume(service, "agent-a", perCall)).Allowed)
                {
                    allowed++;
                }
            }
            return allowed;
        }));
        var allowedCounts = await Task.WhenAll(tasks);

        // 5000 attempts of 7 tokens against 1000 tokens: exactly 142 fit (142 * 7 = 994).
        Assert.Equal(limit / perCall, allowedCounts.Sum());
    }

    [Fact]
    public async Task Negative_tokens_are_rejected()
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Consume(service, "agent-a", -1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_agent_ids_are_rejected(string agentId)
    {
        var service = new InMemoryTokenBudgetService(Limits(100, 1000, 5000), new FakeTimeProvider(_start));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Consume(service, agentId, 1));
    }

    [Fact]
    public void Constructor_rejects_invalid_options()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryTokenBudgetService(Limits(0, 1000, 5000), new FakeTimeProvider(_start)));
    }

    [Fact]
    public void Constructor_rejects_a_null_clock()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new InMemoryTokenBudgetService(Limits(100, 1000, 5000), null!));
    }
}
