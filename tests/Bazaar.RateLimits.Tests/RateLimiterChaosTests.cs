using System.Reflection;
using Bazaar.RateLimits;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.RateLimits.Tests;

/// <summary>
/// Chaos tests (agent-misbehaviour campaign): bursts at the window edge, a clock that steps backwards, many keys, and
/// concurrent callers. The sliding window must never admit more than the limit in any window-length span.
/// </summary>
public sealed class RateLimiterChaosTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static RateLimitPolicy Policy(int limit = 10, TimeSpan? window = null) =>
        new("chaos_policy", limit, window ?? TimeSpan.FromMinutes(1), RateLimitScope.Agent, "chaos.setting");

    [Fact]
    public void A_burst_on_both_sides_of_the_window_edge_admits_at_most_the_limit_per_window()
    {
        var time = new FakeTimeProvider(_start);
        var limiter = new InMemoryRateLimiter(time);
        var policy = Policy(limit: 10);
        var admitted = new List<DateTimeOffset>();

        // 1000 attempts in the last 10 ms before the edge, then 1000 in the first 10 ms after it, 1 ms apart.
        time.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromMilliseconds(10));
        for (var i = 0; i < 2000; i++)
        {
            if (limiter.TryAcquire(policy, "agent:charles").Allowed)
            {
                admitted.Add(time.GetUtcNow());
            }

            if (i % 100 == 99)
            {
                time.Advance(TimeSpan.FromMilliseconds(1));
            }
        }

        // A fixed window would admit 20 here; the sliding log admits 10.
        Assert.Equal(10, admitted.Count);
    }

    [Fact]
    public void A_clock_that_steps_backwards_does_not_reopen_the_window()
    {
        var time = new SteppingClock { Now = _start.AddHours(1) };
        var limiter = new InMemoryRateLimiter(time);
        var policy = Policy(limit: 5);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(policy, "agent:charles").Allowed);
        }

        time.Now = _start.AddMinutes(30);
        var afterStep = limiter.TryAcquire(policy, "agent:charles");

        Assert.False(afterStep.Allowed);
        Assert.True(afterStep.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Many_keys_are_swept_once_their_windows_pass()
    {
        var time = new FakeTimeProvider(_start);
        var limiter = new InMemoryRateLimiter(time);
        var policy = Policy(limit: 1, window: TimeSpan.FromSeconds(1));

        // A runaway agent invents a new scope key on every call.
        for (var i = 0; i < 200_000; i++)
        {
            limiter.TryAcquire(policy, "agent:spam-" + i);
        }

        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 2048; i++)
        {
            limiter.TryAcquire(policy, "agent:steady");
        }

        Assert.True(BucketCount(limiter) < 10, $"{BucketCount(limiter)} buckets are still held.");
    }

    [Fact]
    public async Task Concurrent_callers_never_exceed_the_limit()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider(_start));
        var policy = Policy(limit: 25);

        var results = await Task.WhenAll(Enumerable.Range(0, 2000)
            .Select(_ => Task.Run(() => limiter.TryAcquire(policy, "agent:charles"), TestContext.Current.CancellationToken)));

        Assert.Equal(25, results.Count(result => result.Allowed));
    }

    private static int BucketCount(InMemoryRateLimiter limiter)
    {
        var field = typeof(InMemoryRateLimiter).GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((System.Collections.ICollection)field.GetValue(limiter)!).Count;
    }

    /// <summary>A clock that may step backwards, which <see cref="FakeTimeProvider"/> refuses to do.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
