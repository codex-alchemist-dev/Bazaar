using Bazaar.Server.Gateway.Health;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.Health;

public class CircuitBreakerTests
{
    private static CircuitBreaker Breaker(FakeClock clock, BreakerOptions? options = null) =>
        new(options ?? new BreakerOptions(), clock);

    private static void Fail(CircuitBreaker breaker, int times)
    {
        for (var i = 0; i < times; i++)
        {
            breaker.RecordFailure();
        }
    }

    [Fact]
    public void Closed_opens_at_the_failure_threshold()
    {
        var breaker = Breaker(new FakeClock());

        Fail(breaker, 4);
        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.True(breaker.TryAcquire());

        breaker.RecordFailure();
        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.False(breaker.TryAcquire());
    }

    [Fact]
    public void A_success_resets_the_failure_run()
    {
        var breaker = Breaker(new FakeClock());

        Fail(breaker, 4);
        breaker.RecordSuccess();
        Fail(breaker, 4);
        Assert.Equal(BreakerState.Closed, breaker.State);

        breaker.RecordFailure();
        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void Open_rejects_until_the_open_duration_has_elapsed()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);

        clock.Advance(TimeSpan.FromSeconds(29.999));
        Assert.False(breaker.TryAcquire());
        Assert.False(breaker.WouldAllow());
        Assert.Equal(BreakerState.Open, breaker.State);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void Half_open_admits_exactly_the_trial_count()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock, new BreakerOptions { HalfOpenMaxTrials = 2 });
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breaker.TryAcquire());
        Assert.True(breaker.TryAcquire());
        Assert.False(breaker.TryAcquire());
        Assert.False(breaker.WouldAllow());
    }

    [Fact]
    public void WouldAllow_takes_no_trial()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breaker.WouldAllow());
        Assert.True(breaker.WouldAllow());
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void A_successful_trial_closes_the_breaker()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breaker.TryAcquire());
        breaker.RecordSuccess();

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(0, breaker.Snapshot().ConsecutiveFailures);
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void A_failed_trial_reopens_with_a_fresh_timer()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breaker.TryAcquire());
        breaker.RecordFailure();
        Assert.Equal(BreakerState.Open, breaker.State);

        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(BreakerState.Open, breaker.State);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void Half_open_needs_the_configured_successes_to_close()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock, new BreakerOptions { SuccessesToClose = 2 });
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breaker.TryAcquire());
        breaker.RecordSuccess();
        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        Assert.True(breaker.TryAcquire());
        breaker.RecordSuccess();
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void An_outcome_with_no_trial_taken_does_not_change_a_half_open_breaker()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        breaker.RecordSuccess();
        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        breaker.RecordFailure();
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void A_stale_failure_while_open_does_not_restart_the_timer()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(20));

        breaker.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void Abandoning_a_trial_frees_its_slot()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breaker.TryAcquire());
        Assert.False(breaker.TryAcquire());

        breaker.Abandon();
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void The_snapshot_reports_the_state_run_and_change_time()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock);
        Fail(breaker, 5);

        var snapshot = breaker.Snapshot();

        Assert.Equal(BreakerState.Open, snapshot.State);
        Assert.Equal(5, snapshot.ConsecutiveFailures);
        Assert.Equal(FakeClock.DefaultStart, snapshot.LastChangedAt);
    }

    [Fact]
    public void Concurrent_acquires_in_half_open_never_exceed_the_trial_count()
    {
        var clock = new FakeClock();
        var breaker = Breaker(clock, new BreakerOptions { HalfOpenMaxTrials = 3 });
        Fail(breaker, 5);
        clock.Advance(TimeSpan.FromSeconds(30));

        var granted = 0;
        Parallel.For(0, 1000, _ =>
        {
            if (breaker.TryAcquire())
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(3, granted);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(5, -1)]
    public void Invalid_options_are_rejected(int threshold, int openSeconds)
    {
        var options = new BreakerOptions { FailureThreshold = threshold, OpenDuration = TimeSpan.FromSeconds(openSeconds) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreaker(options, new FakeClock()));
    }
}
