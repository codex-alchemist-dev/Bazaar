using Bazaar.Kodo.Export;
using static Bazaar.Kodo.Tests.Export.KodoExportTestData;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoWebhookRetryPolicyTests
{
    private static readonly TimeSpan _window = TimeSpan.FromHours(24);

    [Fact]
    public void NominalDelayDoublesFromThirtySecondsToTheFourHourCap()
    {
        KodoWebhookRetryPolicy policy = KodoWebhookRetryPolicy.Defaults;

        Assert.Equal(TimeSpan.FromSeconds(30), policy.NominalDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.NominalDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(120), policy.NominalDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(7680), policy.NominalDelay(9));
        Assert.Equal(TimeSpan.FromHours(4), policy.NominalDelay(10));
        Assert.Equal(TimeSpan.FromHours(4), policy.NominalDelay(500));
    }

    [Fact]
    public void ZeroJitterGivesTheExactSchedule()
    {
        IReadOnlyList<TimeSpan> delays = KodoWebhookRetryPolicy.Defaults.PlanDelays(new FixedRandom(0));

        // Nine doubling delays, then four 4 hour delays. The next one would land at 24 hours and 0.5 minutes, past the window.
        Assert.Equal(13, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(7680), delays[8]);
        Assert.Equal(TimeSpan.FromHours(4), delays[9]);
        Assert.Equal(TimeSpan.FromHours(4), delays[12]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(7)]
    public void EveryDelayLiesBetweenItsJitteredAndNominalBound(int seed)
    {
        KodoWebhookRetryPolicy policy = KodoWebhookRetryPolicy.Defaults;

        IReadOnlyList<TimeSpan> delays = policy.PlanDelays(new Random(seed));

        for (int i = 0; i < delays.Count; i++)
        {
            TimeSpan nominal = policy.NominalDelay(i + 1);
            Assert.True(delays[i] <= nominal, $"Delay {i + 1} is above its nominal value.");
            Assert.True(
                delays[i] >= TimeSpan.FromSeconds(nominal.TotalSeconds * (1 - policy.JitterFraction)) - TimeSpan.FromMilliseconds(1),
                $"Delay {i + 1} is below the jitter floor.");
        }
    }

    [Fact]
    public void TheWholeScheduleStaysInsideTheWindowAndNeverIsEmptyOrZero()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            IReadOnlyList<TimeSpan> delays = KodoWebhookRetryPolicy.Defaults.PlanDelays(new Random(seed));

            TimeSpan total = TimeSpan.Zero;
            foreach (TimeSpan delay in delays)
            {
                Assert.True(delay > TimeSpan.Zero);
                total += delay;
            }

            Assert.True(total <= _window, "The schedule ran past the 24 hour window.");
            Assert.True(delays.Count > 0);
        }
    }

    [Fact]
    public void AFullRetryAtExactlyTheWindowIsStillAllowed()
    {
        KodoWebhookRetryPolicy policy = KodoWebhookRetryPolicy.Defaults;
        TimeSpan elapsed = _window - TimeSpan.FromSeconds(30);

        KodoWebhookDecision decision = policy.Decide(KodoWebhookOutcome.RetryableFailure, 1, elapsed, new FixedRandom(0));

        Assert.Equal(KodoWebhookStep.Retry, decision.Step);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.Delay);
    }

    [Fact]
    public void ARetryThatWouldLandPastTheWindowIsDeadLettered()
    {
        KodoWebhookRetryPolicy policy = KodoWebhookRetryPolicy.Defaults;
        TimeSpan elapsed = _window - TimeSpan.FromSeconds(29);

        KodoWebhookDecision decision = policy.Decide(KodoWebhookOutcome.RetryableFailure, 1, elapsed, new FixedRandom(0));

        Assert.Equal(KodoWebhookStep.DeadLetter, decision.Step);
        Assert.Equal("schedule_exhausted", decision.Reason);
    }

    [Fact]
    public void DeliveredStopsTheSchedule()
    {
        KodoWebhookDecision decision = KodoWebhookRetryPolicy.Defaults.Decide(
            KodoWebhookOutcome.Delivered,
            3,
            TimeSpan.FromMinutes(10),
            new FixedRandom(0));

        Assert.Equal(KodoWebhookStep.Done, decision.Step);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
        Assert.Equal("delivered", decision.Reason);
    }

    [Fact]
    public void PermanentFailureDeadLettersAtOnce()
    {
        KodoWebhookDecision decision = KodoWebhookRetryPolicy.Defaults.Decide(
            KodoWebhookOutcome.PermanentFailure,
            1,
            TimeSpan.Zero,
            new FixedRandom(0));

        Assert.Equal(KodoWebhookStep.DeadLetter, decision.Step);
        Assert.Equal("permanent_failure", decision.Reason);
    }

    [Fact]
    public void ARetryableFailureNeedsAtLeastOneFailedAttempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoWebhookRetryPolicy.Defaults.Decide(KodoWebhookOutcome.RetryableFailure, 0, TimeSpan.Zero, new FixedRandom(0)));
    }

    [Fact]
    public void ANegativeElapsedTimeIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoWebhookRetryPolicy.Defaults.Decide(KodoWebhookOutcome.RetryableFailure, 1, TimeSpan.FromSeconds(-1), new FixedRandom(0)));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    public void ScheduleRefusesAMultiplierBelowOne(double multiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KodoWebhookRetryPolicy(TimeSpan.FromSeconds(30), multiplier, TimeSpan.FromHours(4), 0.2, _window));
    }

    [Fact]
    public void ScheduleRefusesJitterOfOneOrMore()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KodoWebhookRetryPolicy(TimeSpan.FromSeconds(30), 2, TimeSpan.FromHours(4), 1.0, _window));
    }

    [Fact]
    public void ScheduleRefusesAWindowShorterThanTheCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KodoWebhookRetryPolicy(TimeSpan.FromSeconds(30), 2, TimeSpan.FromHours(4), 0.2, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ScheduleRefusesACapBelowTheInitialDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new KodoWebhookRetryPolicy(TimeSpan.FromMinutes(5), 2, TimeSpan.FromMinutes(1), 0.2, _window));
    }

    [Theory]
    [InlineData(200, KodoWebhookOutcome.Delivered)]
    [InlineData(204, KodoWebhookOutcome.Delivered)]
    [InlineData(408, KodoWebhookOutcome.RetryableFailure)]
    [InlineData(429, KodoWebhookOutcome.RetryableFailure)]
    [InlineData(500, KodoWebhookOutcome.RetryableFailure)]
    [InlineData(503, KodoWebhookOutcome.RetryableFailure)]
    [InlineData(301, KodoWebhookOutcome.PermanentFailure)]
    [InlineData(400, KodoWebhookOutcome.PermanentFailure)]
    [InlineData(404, KodoWebhookOutcome.PermanentFailure)]
    [InlineData(410, KodoWebhookOutcome.PermanentFailure)]
    public void StatusCodesMapToOutcomes(int statusCode, KodoWebhookOutcome expected)
    {
        Assert.Equal(expected, KodoWebhookOutcomes.FromStatusCode(statusCode));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public void StatusCodesOutsideHttpRangeAreRefused(int statusCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KodoWebhookOutcomes.FromStatusCode(statusCode));
    }
}
