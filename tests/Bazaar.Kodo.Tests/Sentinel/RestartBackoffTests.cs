using Bazaar.Kodo.Sentinel;
using Bazaar.Kodo.Tests.Runtime;

namespace Bazaar.Kodo.Tests.Sentinel;

public sealed class RestartBackoffTests
{
    private static readonly DateTimeOffset _start = RuntimeTestSigning.Now;

    [Fact]
    public void ShippedScheduleGivesOneTwoFiveFifteenSixtyThenCapsAtThreeHundred()
    {
        RestartBackoff backoff = new(RestartBackoff.DefaultScheduleSeconds, RestartBackoff.DefaultStablePeriod);

        List<double> delays = [];
        for (int i = 0; i < 9; i++)
        {
            delays.Add(backoff.RecordExit(_start, _start.AddSeconds(1)).Delay.TotalSeconds);
        }

        Assert.Equal([1d, 2, 5, 15, 60, 300, 300, 300, 300], delays);
        Assert.Equal(TimeSpan.FromSeconds(300), backoff.Cap);
    }

    [Fact]
    public void ExitsAreCountedAndRestartsAreTotalled()
    {
        RestartBackoff backoff = new(RestartBackoff.DefaultScheduleSeconds, RestartBackoff.DefaultStablePeriod);

        RestartDecision first = backoff.RecordExit(_start, _start.AddSeconds(1));
        RestartDecision second = backoff.RecordExit(_start, _start.AddSeconds(1));

        Assert.Equal(1, first.ConsecutiveExits);
        Assert.Equal(1, first.RestartCount);
        Assert.Equal(2, second.ConsecutiveExits);
        Assert.Equal(2, second.RestartCount);
        Assert.Equal(2, backoff.ConsecutiveExits);
        Assert.Equal(2, backoff.RestartCount);
    }

    [Fact]
    public void RunAtLeastTheStablePeriodClearsTheBackoff()
    {
        RestartBackoff backoff = new(RestartBackoff.DefaultScheduleSeconds, TimeSpan.FromMinutes(10));
        backoff.RecordExit(_start, _start.AddSeconds(1));
        backoff.RecordExit(_start, _start.AddSeconds(1));
        backoff.RecordExit(_start, _start.AddSeconds(1));

        RestartDecision afterStable = backoff.RecordExit(_start, _start.AddMinutes(10));

        Assert.Equal(TimeSpan.FromSeconds(1), afterStable.Delay);
        Assert.Equal(1, afterStable.ConsecutiveExits);
        Assert.Equal(4, afterStable.RestartCount);
    }

    [Fact]
    public void RunShorterThanTheStablePeriodKeepsTheBackoff()
    {
        RestartBackoff backoff = new(RestartBackoff.DefaultScheduleSeconds, TimeSpan.FromMinutes(10));
        backoff.RecordExit(_start, _start.AddSeconds(1));

        RestartDecision afterShortRun = backoff.RecordExit(_start, _start.AddMinutes(10).AddSeconds(-1));

        Assert.Equal(TimeSpan.FromSeconds(2), afterShortRun.Delay);
        Assert.Equal(2, afterShortRun.ConsecutiveExits);
    }

    [Fact]
    public void ExitBeforeTheStartIsNotStable()
    {
        RestartBackoff backoff = new(RestartBackoff.DefaultScheduleSeconds, TimeSpan.FromMinutes(10));
        backoff.RecordExit(_start, _start.AddSeconds(1));

        RestartDecision decision = backoff.RecordExit(_start, _start.AddHours(-1));

        Assert.Equal(TimeSpan.FromSeconds(2), decision.Delay);
    }

    [Fact]
    public void CustomScheduleCapsAtItsLastValue()
    {
        RestartBackoff backoff = new([3, 7], TimeSpan.FromMinutes(1));

        List<double> delays = [];
        for (int i = 0; i < 4; i++)
        {
            delays.Add(backoff.RecordExit(_start, _start).Delay.TotalSeconds);
        }

        Assert.Equal([3d, 7, 7, 7], delays);
        Assert.Equal(TimeSpan.FromSeconds(7), backoff.Cap);
    }

    [Fact]
    public void EmptyScheduleIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new RestartBackoff([], TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void DelayBelowOneSecondIsRejected(int seconds)
    {
        Assert.Throws<ArgumentException>(() => new RestartBackoff([1, seconds], TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void DecreasingScheduleIsRejectedBecauseItsLastValueIsTheCap()
    {
        Assert.Throws<ArgumentException>(() => new RestartBackoff([5, 2], TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void NonPositiveStablePeriodIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RestartBackoff([1], TimeSpan.Zero));
    }
}
