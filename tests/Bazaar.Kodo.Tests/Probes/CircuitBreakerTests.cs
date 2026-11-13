using Bazaar.Kodo.Probes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Probes;

public sealed class CircuitBreakerTests
{
    [Fact]
    public void OpensAfterFiveConsecutiveFailures()
    {
        var breaker = new CircuitBreaker(timeProvider: new FakeTimeProvider());

        for (var i = 0; i < 4; i++)
        {
            Assert.True(breaker.TryEnter(out BreakerTicket ticket));
            breaker.Record(ticket, failed: true);
        }

        Assert.Equal(BreakerState.Closed, breaker.State);

        Assert.True(breaker.TryEnter(out BreakerTicket fifth));
        breaker.Record(fifth, failed: true);

        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.False(breaker.TryEnter(out _));
    }

    [Fact]
    public void SuccessResetsTheConsecutiveFailureCount()
    {
        var breaker = new CircuitBreaker(timeProvider: new FakeTimeProvider());

        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.True(breaker.TryEnter(out BreakerTicket ticket));
                breaker.Record(ticket, failed: true);
            }

            Assert.True(breaker.TryEnter(out BreakerTicket ok));
            breaker.Record(ok, failed: false);
        }

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void CooldownMovesOpenToHalfOpen()
    {
        var time = new FakeTimeProvider();
        var breaker = OpenBreaker(time);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.False(breaker.TryEnter(out _));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void PassingTrialClosesTheBreaker()
    {
        var time = new FakeTimeProvider();
        var breaker = OpenBreaker(time);
        time.Advance(TimeSpan.FromSeconds(60));

        Assert.True(breaker.TryEnter(out BreakerTicket trial));
        Assert.True(trial.IsTrial);
        breaker.Record(trial, failed: false);

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.True(breaker.TryEnter(out _));
    }

    [Fact]
    public void FailingTrialReopensWithAFreshCooldown()
    {
        var time = new FakeTimeProvider();
        var breaker = OpenBreaker(time);
        time.Advance(TimeSpan.FromSeconds(60));

        Assert.True(breaker.TryEnter(out BreakerTicket trial));
        breaker.Record(trial, failed: true);
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public async Task ExactlyOneTrialIsAllowedWhenTwentyCallersRace()
    {
        var time = new FakeTimeProvider();
        var breaker = OpenBreaker(time);
        time.Advance(TimeSpan.FromSeconds(60));

        // All 20 tasks wait on one gate, then call TryEnter together. The first trial stays in flight
        // (it is not recorded), so every other caller must be refused.
        using var gate = new ManualResetEventSlim(initialState: false);
        Task<bool>[] callers = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() =>
            {
                gate.Wait();
                return breaker.TryEnter(out _);
            }))
            .ToArray();

        gate.Set();
        bool[] admitted = await Task.WhenAll(callers);

        Assert.Equal(1, admitted.Count(a => a));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void ReleasedTrialLetsAnotherCallerTry()
    {
        var time = new FakeTimeProvider();
        var breaker = OpenBreaker(time);
        time.Advance(TimeSpan.FromSeconds(60));

        Assert.True(breaker.TryEnter(out BreakerTicket first));
        Assert.False(breaker.TryEnter(out _));

        breaker.ReleaseTrial(first);

        Assert.Equal(BreakerState.HalfOpen, breaker.State);
        Assert.True(breaker.TryEnter(out BreakerTicket second));
        Assert.True(second.IsTrial);
    }

    [Fact]
    public void StaleTicketCannotChangeANewerState()
    {
        var breaker = new CircuitBreaker(failureThreshold: 1, timeProvider: new FakeTimeProvider());

        Assert.True(breaker.TryEnter(out BreakerTicket slowClosedCall));
        Assert.True(breaker.TryEnter(out BreakerTicket fastCall));
        breaker.Record(fastCall, failed: true);
        Assert.Equal(BreakerState.Open, breaker.State);

        // The slow call succeeds after the breaker opened. Its ticket is stale, so the breaker stays open.
        breaker.Record(slowClosedCall, failed: false);

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    private static CircuitBreaker OpenBreaker(FakeTimeProvider time)
    {
        var breaker = new CircuitBreaker(timeProvider: time);
        for (var i = 0; i < CircuitBreaker.DefaultFailureThreshold; i++)
        {
            Assert.True(breaker.TryEnter(out BreakerTicket ticket));
            breaker.Record(ticket, failed: true);
        }

        Assert.Equal(BreakerState.Open, breaker.State);
        return breaker;
    }
}
