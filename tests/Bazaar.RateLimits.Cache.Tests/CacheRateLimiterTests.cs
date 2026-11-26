using Bazaar.Server.Providers;
using Bazaar.Server.RateLimits;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.RateLimits.Cache.Tests;

public sealed class CacheRateLimiterTests
{
    private static readonly RateLimitPolicy _policy = new("test_policy", 2, TimeSpan.FromSeconds(10), RateLimitScope.Agent, "test.key");
    private static readonly DateTimeOffset _start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(_start);
    private readonly FakeCounter _counter = new();
    private readonly ListLogger _log = new();

    private CacheRateLimiter Create(FakeCounter? counter = null, TimeSpan? timeout = null, bool noCounter = false)
    {
        var chosen = counter ?? _counter;
        return new CacheRateLimiter(
            () => noCounter ? null : chosen,
            new CacheRateLimiterOptions { CacheTimeout = timeout ?? TimeSpan.FromSeconds(2) },
            _time,
            _log);
    }

    [Fact]
    public void Admits_up_to_the_limit_then_denies_without_counting_the_denial()
    {
        var limiter = Create();

        var first = limiter.TryAcquire(_policy, "agent-1");
        var second = limiter.TryAcquire(_policy, "agent-1");
        var third = limiter.TryAcquire(_policy, "agent-1");
        var fourth = limiter.TryAcquire(_policy, "agent-1");

        Assert.True(first.Allowed);
        Assert.Equal(1, first.Remaining);
        Assert.True(second.Allowed);
        Assert.Equal(0, second.Remaining);
        Assert.False(third.Allowed);
        Assert.False(fourth.Allowed);
        Assert.Equal(0, third.Remaining);
        Assert.True(third.RetryAfter > TimeSpan.Zero);
        Assert.Equal(2, _counter.CountOfCurrentBucket());
    }

    [Fact]
    public void The_window_slides_with_the_clock()
    {
        var limiter = Create();
        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);

        // Next bucket, start: the previous bucket (2 events) still weighs fully.
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);

        // Half way: the previous bucket weighs 1, so one slot is free.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);

        // Two windows after the burst, nothing of it is left.
        _time.Advance(TimeSpan.FromSeconds(25));
        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);
    }

    [Fact]
    public void Retry_after_names_a_time_after_which_a_call_is_admitted()
    {
        var limiter = Create();
        limiter.TryAcquire(_policy, "a");
        limiter.TryAcquire(_policy, "a");
        _time.Advance(TimeSpan.FromSeconds(10));
        var denied = limiter.TryAcquire(_policy, "a");
        Assert.False(denied.Allowed);

        _time.Advance(denied.RetryAfter);

        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
    }

    [Fact]
    public void Scopes_and_policies_are_isolated()
    {
        var limiter = Create();
        var other = _policy with { Name = "other_policy" };
        limiter.TryAcquire(_policy, "a");
        limiter.TryAcquire(_policy, "a");

        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.True(limiter.TryAcquire(_policy, "b").Allowed);
        Assert.True(limiter.TryAcquire(other, "a").Allowed);
    }

    [Fact]
    public void Colliding_looking_names_use_different_keys()
    {
        var limiter = Create();
        var colon = _policy with { Name = "a:b" };
        var plain = _policy with { Name = "a" };

        limiter.TryAcquire(colon, "c");
        limiter.TryAcquire(colon, "c");

        Assert.True(limiter.TryAcquire(plain, "b:c").Allowed);
    }

    [Fact]
    public void Two_limiters_on_one_counter_share_their_counts()
    {
        var one = Create();
        var two = Create();

        Assert.True(one.TryAcquire(_policy, "a").Allowed);
        Assert.True(two.TryAcquire(_policy, "a").Allowed);
        Assert.False(one.TryAcquire(_policy, "a").Allowed);
    }

    [Fact]
    public void A_new_limiter_on_the_same_cache_keeps_the_counts_like_after_a_restart()
    {
        Assert.True(Create().TryAcquire(_policy, "a").Allowed);
        Assert.True(Create().TryAcquire(_policy, "a").Allowed);

        Assert.False(Create().TryAcquire(_policy, "a").Allowed);
    }

    [Fact]
    public void Every_bucket_expires_after_twice_the_window()
    {
        var limiter = Create();

        limiter.TryAcquire(_policy, "a");

        Assert.Contains(_counter.Ttls, t => t.Value == TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_timeout_falls_back_to_the_in_process_limiter_warns_once_and_recovers()
    {
        var limiter = Create(timeout: TimeSpan.FromMilliseconds(50));
        _counter.Hang = true;

        var first = limiter.TryAcquire(_policy, "secret-agent-id");
        var second = limiter.TryAcquire(_policy, "secret-agent-id");
        var third = limiter.TryAcquire(_policy, "secret-agent-id");

        // The in-process fallback enforces the limit while the cache is out: never fail open.
        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(third.Allowed);
        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("timeout", warning.Message, StringComparison.Ordinal);

        _counter.Hang = false;
        var back = limiter.TryAcquire(_policy, "secret-agent-id");

        Assert.True(back.Allowed);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Information);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("secret-agent-id", StringComparison.Ordinal)
            || e.Message.Contains(_policy.Name, StringComparison.Ordinal) || e.Message.Contains("ratelimit:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_throwing_counter_falls_back_without_throwing_or_logging_the_message()
    {
        var limiter = Create();
        _counter.Failure = new IOException("connection to redis.internal:6379 refused for secret-agent-id");

        var decision = limiter.TryAcquire(_policy, "secret-agent-id");
        limiter.TryAcquire(_policy, "secret-agent-id");
        var denied = limiter.TryAcquire(_policy, "secret-agent-id");

        Assert.True(decision.Allowed);
        Assert.False(denied.Allowed);
        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(nameof(IOException), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("secret-agent-id", StringComparison.Ordinal)
            || e.Message.Contains("redis.internal", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Exception is not null);
    }

    [Fact]
    public void A_second_outage_warns_again_after_a_recovery()
    {
        var limiter = Create();
        _counter.Failure = new IOException();
        limiter.TryAcquire(_policy, "a");
        _counter.Failure = null;
        limiter.TryAcquire(_policy, "b");
        _counter.Failure = new IOException();
        limiter.TryAcquire(_policy, "c");

        Assert.Equal(2, _log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public void With_no_counter_the_in_process_limiter_decides_and_one_warning_is_logged()
    {
        var limiter = Create(noCounter: true);

        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);

        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void A_failed_undo_still_denies_and_leaves_the_bucket_high_never_open()
    {
        var limiter = Create();
        limiter.TryAcquire(_policy, "a");
        limiter.TryAcquire(_policy, "a");
        _counter.FailOnDecrement = true;

        var denied = limiter.TryAcquire(_policy, "a");

        Assert.False(denied.Allowed);
        Assert.Equal(3, _counter.CountOfCurrentBucket());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Bad_arguments_throw_like_the_in_memory_limiter()
    {
        var limiter = Create();

        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(_policy, " "));
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!, "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire(_policy with { Limit = 0 }, "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.TryAcquire(_policy with { Window = TimeSpan.Zero }, "a"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void The_cache_timeout_is_validated(int milliseconds)
    {
        var options = new CacheRateLimiterOptions { CacheTimeout = TimeSpan.FromMilliseconds(milliseconds) };

        Assert.NotNull(options.ValidationError());
        Assert.Throws<ArgumentException>(() => new CacheRateLimiter(() => null, options, _time, _log));
    }

    [Fact]
    public void The_default_cache_timeout_is_150_milliseconds_and_valid()
    {
        var options = new CacheRateLimiterOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(150), options.CacheTimeout);
        Assert.Null(options.ValidationError());
    }

    private sealed class FakeCounter : ICacheCounter
    {
        private readonly Lock _gate = new();

        public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, TimeSpan> Ttls { get; } = new(StringComparer.Ordinal);

        public bool Hang { get; set; }

        public bool FailOnDecrement { get; set; }

        public Exception? Failure { get; set; }

        public long CountOfCurrentBucket() => Counts.Values.Max();

        public Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct)
        {
            if (Failure is not null)
            {
                return Task.FromException<long>(Failure);
            }

            if (Hang)
            {
                return new TaskCompletionSource<long>().Task;
            }

            if (FailOnDecrement && by < 0)
            {
                return Task.FromException<long>(new IOException("undo failed"));
            }

            lock (_gate)
            {
                Counts[key] = Counts.GetValueOrDefault(key) + by;
                Ttls.TryAdd(key, ttl);
                return Task.FromResult(Counts[key]);
            }
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
