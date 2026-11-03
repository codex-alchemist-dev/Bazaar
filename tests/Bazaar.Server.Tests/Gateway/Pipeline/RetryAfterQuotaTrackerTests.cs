using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Pipeline;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.Pipeline;

public class RetryAfterQuotaTrackerTests
{
    private static readonly ProviderKey _keyA = new("provider-x", "secret://bazaar/providers/x-a/key");
    private static readonly ProviderKey _keyB = new("provider-x", "secret://bazaar/providers/x-b/key");

    private static ModelEvent.Failure RateLimited(int? retryAfter = null) => new("rate_limited", "slow down", retryAfter);

    private static ModelEvent.Failure SpendCapped(int? retryAfter = null) => new("spend_capped", "limit reached", retryAfter);

    [Fact]
    public void Retry_after_blocks_until_the_retry_time_then_expires()
    {
        var clock = new FakeClock();
        var tracker = new RetryAfterQuotaTracker(clock);
        var start = clock.GetUtcNow();

        tracker.Report(_keyA, "claude", RateLimited(60));

        Assert.Equal(QuotaState.RateLimited, tracker.BlockedState(_keyA, "claude"));
        Assert.Equal(start.AddSeconds(60), tracker.BlockedUntil(_keyA, "claude"));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(QuotaState.RateLimited, tracker.BlockedState(_keyA, "claude"));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(tracker.BlockedState(_keyA, "claude"));
        Assert.Null(tracker.BlockedUntil(_keyA, "claude"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Rate_limit_without_a_usable_retry_time_gives_no_block(int? retryAfter)
    {
        var tracker = new RetryAfterQuotaTracker(new FakeClock());

        tracker.Report(_keyA, "claude", RateLimited(retryAfter));

        Assert.Null(tracker.BlockedState(_keyA, "claude"));
        Assert.Null(tracker.BlockedUntil(_keyA, "claude"));
    }

    [Fact]
    public void Non_quota_failures_never_block_on_quota()
    {
        var tracker = new RetryAfterQuotaTracker(new FakeClock());

        tracker.Report(_keyA, "claude", new ModelEvent.Failure("provider_error", "boom", 30));
        tracker.Report(_keyA, "claude", new ModelEvent.Failure("timeout", "late", 30));

        Assert.Null(tracker.BlockedState(_keyA, "claude"));
    }

    [Fact]
    public void Spend_cap_blocks_until_midnight_utc_on_the_first_of_next_month_and_ignores_retry_after()
    {
        var clock = new FakeClock();
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var tracker = new RetryAfterQuotaTracker(clock);
        var nextReset = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Report(_keyA, "claude", SpendCapped(retryAfter: 30));

        Assert.Equal(QuotaState.SpendCapped, tracker.BlockedState(_keyA, "claude"));
        Assert.Equal(nextReset, tracker.BlockedUntil(_keyA, "claude"));

        // Well past the 30 seconds a retry timer would have allowed: still capped.
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(QuotaState.SpendCapped, tracker.BlockedState(_keyA, "claude"));

        clock.SetUtcNow(nextReset.AddSeconds(-1));
        Assert.Equal(QuotaState.SpendCapped, tracker.BlockedState(_keyA, "claude"));

        clock.SetUtcNow(nextReset);
        Assert.Null(tracker.BlockedState(_keyA, "claude"));
    }

    [Fact]
    public void Spend_cap_in_december_rolls_over_to_january()
    {
        var clock = new FakeClock();
        clock.SetUtcNow(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero));
        var tracker = new RetryAfterQuotaTracker(clock);

        tracker.Report(_keyA, "claude", SpendCapped());

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), tracker.BlockedUntil(_keyA, "claude"));
    }

    [Fact]
    public void The_longer_block_wins_when_a_spend_cap_and_a_retry_timer_overlap()
    {
        var clock = new FakeClock();
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var tracker = new RetryAfterQuotaTracker(clock);

        tracker.Report(_keyA, "claude", SpendCapped());
        tracker.Report(_keyA, "claude", RateLimited(60));

        Assert.Equal(QuotaState.SpendCapped, tracker.BlockedState(_keyA, "claude"));
    }

    [Fact]
    public void A_spend_cap_replaces_a_shorter_retry_timer()
    {
        var clock = new FakeClock();
        clock.SetUtcNow(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var tracker = new RetryAfterQuotaTracker(clock);

        tracker.Report(_keyA, "claude", RateLimited(60));
        tracker.Report(_keyA, "claude", SpendCapped());

        Assert.Equal(QuotaState.SpendCapped, tracker.BlockedState(_keyA, "claude"));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), tracker.BlockedUntil(_keyA, "claude"));
    }

    [Fact]
    public void Blocks_are_per_provider_key_and_model_class()
    {
        var tracker = new RetryAfterQuotaTracker(new FakeClock());

        tracker.Report(_keyA, "claude", RateLimited(60));

        Assert.Equal(QuotaState.RateLimited, tracker.BlockedState(_keyA, "claude"));
        Assert.Null(tracker.BlockedState(_keyA, "gemini"));
        Assert.Null(tracker.BlockedState(_keyB, "claude"));
    }

    [Fact]
    public void A_shorter_retry_timer_does_not_shorten_an_existing_block()
    {
        var tracker = new RetryAfterQuotaTracker(new FakeClock());

        tracker.Report(_keyA, "claude", RateLimited(60));
        tracker.Report(_keyA, "claude", RateLimited(30));

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero), tracker.BlockedUntil(_keyA, "claude"));
    }

    [Fact]
    public void Reporting_a_null_failure_is_rejected()
    {
        var tracker = new RetryAfterQuotaTracker(new FakeClock());

        Assert.Throws<ArgumentNullException>(() => tracker.Report(_keyA, "claude", null!));
    }
}
