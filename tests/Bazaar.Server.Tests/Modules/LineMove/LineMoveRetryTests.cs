using Bazaar.Server.Modules.LineMove;

namespace Bazaar.Server.Tests.Modules.LineMove;

public sealed class LineMoveRetryTests
{
    private static LineMoveRetryRunner Retrying(FakeSteps steps, InMemoryAuditSink audit, LineMoveRetryPolicy policy, List<TimeSpan> delays)
    {
        var runner = new LineMoveRunner(steps.Build(), audit, LineMoveTestData.Clock());
        return new LineMoveRetryRunner(runner, policy, (wait, _) =>
        {
            delays.Add(wait);
            return Task.CompletedTask;
        });
    }

    private static LineMoveRetryPolicy Policy(int maxAttempts = 3, double multiplier = 2.0, int maxMinutes = 60) =>
        new(maxAttempts, TimeSpan.FromMinutes(10), multiplier, TimeSpan.FromMinutes(maxMinutes));

    [Fact]
    public async Task A_rolled_back_move_is_retried_and_completes_on_a_later_attempt()
    {
        var verifyCalls = 0;
        var steps = new FakeSteps().Script(LineMoveStep.Verify, (_, _) =>
        {
            verifyCalls++;
            return Task.FromResult(verifyCalls == 1 ? StepResult.Failure("Health contract red once.") : StepResult.Success("Verify passed."));
        });
        var delays = new List<TimeSpan>();

        var outcome = await Retrying(steps, new InMemoryAuditSink(), Policy(), delays)
            .RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.Equal(2, outcome.Attempts);
        Assert.False(outcome.Halted);
        Assert.Equal(LineMoveStatus.Completed, outcome.Last.Status);
        Assert.Equal([TimeSpan.FromMinutes(10)], delays);
    }

    [Fact]
    public async Task A_move_that_rolls_back_every_time_halts_after_the_last_attempt()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.Verify, "Health red.");
        var delays = new List<TimeSpan>();

        var outcome = await Retrying(steps, new InMemoryAuditSink(), Policy(maxAttempts: 3), delays)
            .RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.Equal(3, outcome.Attempts);
        Assert.True(outcome.Halted);
        Assert.Equal(LineMoveStatus.RolledBack, outcome.Last.Status);
        // Two waits between three attempts: 10 minutes, then 20 minutes.
        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)], delays);
    }

    [Fact]
    public async Task Backoff_never_exceeds_the_maximum()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.Verify, "Health red.");
        var delays = new List<TimeSpan>();

        await Retrying(steps, new InMemoryAuditSink(), Policy(maxAttempts: 4, maxMinutes: 15), delays)
            .RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)], delays);
    }

    [Fact]
    public async Task A_rollback_outside_the_window_is_not_retried()
    {
        var steps = new FakeSteps().Fail(LineMoveStep.Verify, "Health red.");
        var delays = new List<TimeSpan>();

        var outcome = await Retrying(steps, new InMemoryAuditSink(), Policy(), delays)
            .RunAsync(LineMoveTestData.Request(windowOpen: false), CancellationToken.None);

        Assert.Equal(1, outcome.Attempts);
        Assert.False(outcome.Halted);
        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Last.Status);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task A_failed_preflight_is_not_retried()
    {
        var preflight = LineMoveTestData.HealthyPreflight() with { KodoGreenOnCurrentLine = false };
        var delays = new List<TimeSpan>();

        var outcome = await Retrying(new FakeSteps(), new InMemoryAuditSink(), Policy(), delays)
            .RunAsync(LineMoveTestData.Request(preflight: preflight), CancellationToken.None);

        Assert.Equal(1, outcome.Attempts);
        Assert.False(outcome.Halted);
        Assert.Equal(LineMoveStatus.Failed, outcome.Last.Status);
        Assert.Empty(delays);
    }

    [Fact]
    public void Default_policy_waits_ten_then_twenty_then_forty_minutes_capped_at_an_hour()
    {
        var policy = LineMoveRetryPolicy.Default;

        Assert.Equal(TimeSpan.FromMinutes(10), policy.BackoffAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(20), policy.BackoffAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(40), policy.BackoffAfter(3));
        Assert.Equal(TimeSpan.FromHours(1), policy.BackoffAfter(4));
    }

    [Theory]
    [InlineData(0, 2.0)]
    [InlineData(11, 2.0)]
    [InlineData(3, 0.5)]
    [InlineData(3, double.NaN)]
    public void Out_of_range_policy_values_are_refused(int maxAttempts, double multiplier)
    {
        var policy = new LineMoveRetryPolicy(maxAttempts, TimeSpan.FromMinutes(10), multiplier, TimeSpan.FromHours(1));

        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => policy.Validated());
    }

    [Fact]
    public void A_maximum_shorter_than_the_initial_backoff_is_refused()
    {
        var policy = new LineMoveRetryPolicy(3, TimeSpan.FromMinutes(10), 2.0, TimeSpan.FromMinutes(5));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validated());
    }
}
