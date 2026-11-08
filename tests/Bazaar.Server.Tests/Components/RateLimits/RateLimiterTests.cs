using Bazaar.Server.Components.RateLimits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Components.RateLimits;

/// <summary>Sliding-window behaviour with a fake clock. Pure: no database, no real time.</summary>
public sealed class RateLimiterTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static RateLimitPolicy Policy(int limit = 3, TimeSpan? window = null) =>
        new("test_policy", limit, window ?? TimeSpan.FromMinutes(1), RateLimitScope.Agent, "test.setting");

    [Fact]
    public void Allows_up_to_limit_then_denies()
    {
        var time = new FakeTimeProvider(_start);
        var limiter = new InMemoryRateLimiter(time);
        var policy = Policy(limit: 3);

        var first = limiter.TryAcquire(policy, "agent:charles");
        var second = limiter.TryAcquire(policy, "agent:charles");
        var third = limiter.TryAcquire(policy, "agent:charles");
        var fourth = limiter.TryAcquire(policy, "agent:charles");

        Assert.True(first.Allowed);
        Assert.Equal(2, first.Remaining);
        Assert.True(second.Allowed);
        Assert.Equal(1, second.Remaining);
        Assert.True(third.Allowed);
        Assert.Equal(0, third.Remaining);
        Assert.False(fourth.Allowed);
        Assert.Equal(0, fourth.Remaining);
    }

    [Fact]
    public void Denied_call_reports_retry_after_until_oldest_event_leaves_window()
    {
        var time = new FakeTimeProvider(_start);
        var limiter = new InMemoryRateLimiter(time);
        var policy = Policy(limit: 2, window: TimeSpan.FromMinutes(10));

        limiter.TryAcquire(policy, "k");                 // admitted at +0 min
        time.Advance(TimeSpan.FromMinutes(4));
        limiter.TryAcquire(policy, "k");                 // admitted at +4 min

        time.Advance(TimeSpan.FromMinutes(3));           // now +7 min
        var denied = limiter.TryAcquire(policy, "k");

        Assert.False(denied.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(3), denied.RetryAfter);
    }

    [Fact]
    public void Window_slides_so_a_slot_frees_when_the_oldest_event_expires()
    {
        var time = new FakeTimeProvider(_start);
        var limiter = new InMemoryRateLimiter(time);
        var policy = Policy(limit: 2, window: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(policy, "k").Allowed);   // +0 s
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(limiter.TryAcquire(policy, "k").Allowed);   // +30 s
        Assert.False(limiter.TryAcquire(policy, "k").Allowed);  // +30 s, still full

        time.Advance(TimeSpan.FromSeconds(29));                 // +59 s: first event still inside
        Assert.False(limiter.TryAcquire(policy, "k").Allowed);

        time.Advance(TimeSpan.FromSeconds(1));                  // +60 s: first event has left the window
        var reopened = limiter.TryAcquire(policy, "k");
        Assert.True(reopened.Allowed);
        Assert.Equal(0, reopened.Remaining);                    // the +30 s event still counts
    }

    [Fact]
    public void Denied_calls_are_not_counted()
    {
        var time = new FakeTimeProvider(_start);
        var limiter = new InMemoryRateLimiter(time);
        var policy = Policy(limit: 1, window: TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(policy, "k").Allowed);   // +0 s
        for (var i = 0; i < 5; i++)
        {
            Assert.False(limiter.TryAcquire(policy, "k").Allowed);
        }

        // Retry hammering must not push the reopening time later: only the +0 s admission counts.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(policy, "k").Allowed);
    }

    [Fact]
    public void Retry_after_is_zero_when_allowed()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider(_start));

        var decision = limiter.TryAcquire(Policy(limit: 5), "k");

        Assert.True(decision.Allowed);
        Assert.Equal(TimeSpan.Zero, decision.RetryAfter);
    }

    [Fact]
    public void Keys_are_isolated()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider(_start));
        var policy = Policy(limit: 1);

        Assert.True(limiter.TryAcquire(policy, "agent:charles").Allowed);
        Assert.False(limiter.TryAcquire(policy, "agent:charles").Allowed);
        Assert.True(limiter.TryAcquire(policy, "agent:juniper").Allowed);
    }

    [Fact]
    public void Policies_are_isolated_by_name_for_the_same_key()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider(_start));
        var mail = new RateLimitPolicy("mail", 1, TimeSpan.FromDays(1), RateLimitScope.Agent, "security.quota.mail_sent_per_day");
        var approvals = new RateLimitPolicy("approvals", 1, TimeSpan.FromHours(1), RateLimitScope.Agent, "security.approval.request_cap_per_agent_hour");

        Assert.True(limiter.TryAcquire(mail, "agent:charles").Allowed);
        Assert.False(limiter.TryAcquire(mail, "agent:charles").Allowed);
        Assert.True(limiter.TryAcquire(approvals, "agent:charles").Allowed);
    }

    [Fact]
    public void Blank_scope_key_is_rejected()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider(_start));

        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(Policy(), " "));
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(Policy(), null!));
    }

    [Fact]
    public void Invalid_policy_is_rejected()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider(_start));

        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire(Policy(limit: 0), "k"));
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire(Policy(window: TimeSpan.Zero), "k"));
    }

    [Fact]
    public async Task Concurrent_calls_never_exceed_the_limit()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider(_start));
        var policy = Policy(limit: 30, window: TimeSpan.FromMinutes(1));

        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 500).Select(_ => Task.Run(() => limiter.TryAcquire(policy, "installation"))));

        Assert.Equal(30, decisions.Count(d => d.Allowed));
        Assert.Equal(470, decisions.Count(d => !d.Allowed));
    }

    [Fact]
    public void Shared_policies_match_doc_15_quota_table()
    {
        Assert.Equal(5, SharedRateLimitPolicies.PublicItemsPerPlatformDay.Limit);
        Assert.Equal(TimeSpan.FromDays(1), SharedRateLimitPolicies.PublicItemsPerPlatformDay.Window);
        Assert.Equal(RateLimitScope.Platform, SharedRateLimitPolicies.PublicItemsPerPlatformDay.Scope);

        Assert.Equal(1, SharedRateLimitPolicies.PublicIssueOrPrPerRepositoryDay.Limit);
        Assert.Equal(RateLimitScope.Repository, SharedRateLimitPolicies.PublicIssueOrPrPerRepositoryDay.Scope);

        Assert.Equal(3, SharedRateLimitPolicies.NewRepositoriesPerWeek.Limit);
        Assert.Equal(TimeSpan.FromDays(7), SharedRateLimitPolicies.NewRepositoriesPerWeek.Window);
        Assert.Equal(RateLimitScope.Installation, SharedRateLimitPolicies.NewRepositoriesPerWeek.Scope);

        Assert.Equal(20, SharedRateLimitPolicies.MailSentPerDay.Limit);
        Assert.Equal(RateLimitScope.Agent, SharedRateLimitPolicies.MailSentPerDay.Scope);

        Assert.Equal(30, SharedRateLimitPolicies.GitHubWritesPerMinute.Limit);
        Assert.Equal(TimeSpan.FromMinutes(1), SharedRateLimitPolicies.GitHubWritesPerMinute.Window);
        Assert.Equal(RateLimitScope.Installation, SharedRateLimitPolicies.GitHubWritesPerMinute.Scope);

        Assert.Equal(20, SharedRateLimitPolicies.SkimmerScoredWakesPerHour.Limit);
        Assert.Equal(TimeSpan.FromHours(1), SharedRateLimitPolicies.SkimmerScoredWakesPerHour.Window);

        Assert.Equal(10, SharedRateLimitPolicies.ApprovalRequestsPerAgentHour.Limit);
        Assert.Equal("security.approval.request_cap_per_agent_hour", SharedRateLimitPolicies.ApprovalRequestsPerAgentHour.SettingKey);
    }

    [Fact]
    public void AddBazaarRateLimits_registers_a_singleton_limiter_with_system_clock()
    {
        var services = new ServiceCollection().AddBazaarRateLimits();

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IRateLimiter>();
        var second = provider.GetRequiredService<IRateLimiter>();

        Assert.IsType<InMemoryRateLimiter>(first);
        Assert.Same(first, second);
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void AddBazaarRateLimits_keeps_a_host_registered_time_provider()
    {
        var fake = new FakeTimeProvider(_start);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fake);
        services.AddBazaarRateLimits();

        using var provider = services.BuildServiceProvider();

        Assert.Same(fake, provider.GetRequiredService<TimeProvider>());
    }
}
