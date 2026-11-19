using Bazaar.Server.Modules.LineMove;

namespace Bazaar.Server.Modules.Tests.LineMove;

/// <summary>
/// Update and rollback chaos (doc 42 section 10.3, doc 46 section 17.1): an executor that throws at each step,
/// a rollback that itself fails or throws, executor-side timeouts, repeated rollbacks and the retry bound.
/// A crashed step must be audited and must follow the same recovery as a failed one; nothing may escape as an exception.
/// </summary>
public sealed class LineMoveChaosTests
{
    private static readonly LineMoveStep[] _executed =
        [LineMoveStep.Snapshot, LineMoveStep.DryRun, LineMoveStep.Switch, LineMoveStep.Verify, LineMoveStep.Complete];

    private static LineMoveRunner Runner(FakeSteps steps, InMemoryAuditSink audit) =>
        new(steps.Build(), audit, LineMoveTestData.Clock());

    [Theory]
    [InlineData(LineMoveStep.Snapshot)]
    [InlineData(LineMoveStep.DryRun)]
    [InlineData(LineMoveStep.Switch)]
    [InlineData(LineMoveStep.Verify)]
    [InlineData(LineMoveStep.Complete)]
    public async Task AnExecutorThatThrowsAtAnyStepIsAuditedAndNeverEscapes(LineMoveStep crashAt)
    {
        FakeSteps steps = new FakeSteps().Script(crashAt, (_, _) => throw new IOException("disk went away"));
        InMemoryAuditSink audit = new();

        LineMoveOutcome outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.NotEqual(LineMoveStatus.Completed, outcome.Status);
        Assert.Contains(audit.Events, e => e.Step == crashAt && e.Kind == LineMoveAuditKind.Failed);
        Assert.Empty(outcome.RoleAssignments);

        // A crash at or after the switch may have half-changed the data directory, so it recovers like a failed Verify.
        if (crashAt is LineMoveStep.Switch or LineMoveStep.Verify)
        {
            Assert.Equal(LineMoveStatus.RolledBack, outcome.Status);
            Assert.Contains(LineMoveStep.RolledBack, steps.Ran);
        }
        else
        {
            Assert.DoesNotContain(LineMoveStep.RolledBack, steps.Ran);
        }

        // Nothing after the crashed step ran, except the rollback.
        int crashedIndex = Array.IndexOf(_executed, crashAt);
        Assert.All(steps.Ran.Where(s => s != LineMoveStep.RolledBack), s => Assert.True(Array.IndexOf(_executed, s) <= crashedIndex));
    }

    [Theory]
    [InlineData(LineMoveStep.Switch)]
    [InlineData(LineMoveStep.Verify)]
    public async Task ACrashInsideTheSwitchWindowAfterItClosedNeedsAManualRollback(LineMoveStep crashAt)
    {
        FakeSteps steps = new FakeSteps().Script(crashAt, (_, _) => throw new InvalidOperationException("boom"));
        InMemoryAuditSink audit = new();

        LineMoveOutcome outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(windowOpen: false), CancellationToken.None);

        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Status);
        Assert.True(outcome.RollbackRequired);
        Assert.DoesNotContain(LineMoveStep.RolledBack, steps.Ran);
    }

    [Fact]
    public async Task ARollbackThatThrowsIsAManualRollbackNotAnException()
    {
        FakeSteps steps = new FakeSteps()
            .Fail(LineMoveStep.Verify, "red")
            .Script(LineMoveStep.RolledBack, (_, _) => throw new IOException("restore failed"));
        InMemoryAuditSink audit = new();

        LineMoveOutcome outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(windowOpen: true), CancellationToken.None);

        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Status);
        Assert.True(outcome.RollbackRequired);
        Assert.Contains(audit.Events, e => e.Step == LineMoveStep.RolledBack && e.Kind == LineMoveAuditKind.ManualRollbackRequired);
    }

    [Fact]
    public async Task ARollbackThatReportsFailureIsAManualRollbackAndIsNotRetriedByTheRunner()
    {
        FakeSteps steps = new FakeSteps()
            .Fail(LineMoveStep.Verify, "red")
            .Fail(LineMoveStep.RolledBack, "snapshot corrupt");

        LineMoveOutcome outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Status);
        Assert.Single(steps.Ran, s => s == LineMoveStep.RolledBack);
    }

    [Fact]
    public async Task AnExecutorSideTimeoutIsAFailureNotACancellation()
    {
        // An OperationCanceledException that did not come from the caller's token (an inner HTTP timeout, say) is a failed step.
        FakeSteps steps = new FakeSteps().Script(LineMoveStep.Switch, (_, _) => throw new OperationCanceledException("inner timeout"));
        InMemoryAuditSink audit = new();

        LineMoveOutcome outcome = await Runner(steps, audit).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(LineMoveStatus.RolledBack, outcome.Status);
        Assert.Contains(audit.Events, e => e.Step == LineMoveStep.Switch && e.Kind == LineMoveAuditKind.Failed);
    }

    [Fact]
    public async Task TheAuditDetailOfACrashNamesTheTypeNotTheMessage()
    {
        // The exception text can carry connection strings or paths, so only the type name is audited.
        FakeSteps steps = new FakeSteps().Script(LineMoveStep.DryRun, (_, _) => throw new InvalidOperationException("Host=db;Password=hunter2"));
        InMemoryAuditSink audit = new();

        await Runner(steps, audit).RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        LineMoveAuditEvent failed = Assert.Single(audit.Events, e => e.Kind == LineMoveAuditKind.Failed);
        Assert.DoesNotContain("hunter2", failed.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), failed.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LineMoveStep.Snapshot)]
    [InlineData(LineMoveStep.DryRun)]
    [InlineData(LineMoveStep.Switch)]
    [InlineData(LineMoveStep.Verify)]
    public async Task CancellationAtEveryStepClaimsNoSuccessAndFlagsRollbackFromTheSwitchOn(LineMoveStep cancelAt)
    {
        using CancellationTokenSource cts = new();
        FakeSteps steps = new FakeSteps().Script(cancelAt, (_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        LineMoveOutcome outcome = await Runner(steps, new InMemoryAuditSink()).RunAsync(LineMoveTestData.Request(), cts.Token);

        Assert.Equal(LineMoveStatus.Cancelled, outcome.Status);
        Assert.Equal(cancelAt is LineMoveStep.Switch or LineMoveStep.Verify, outcome.RollbackRequired);
        Assert.Empty(outcome.RoleAssignments);
    }

    [Fact]
    public async Task RollbackOfARollbackNeverMakesTheSecondAttemptSkipPreflight()
    {
        // Every attempt crashes at the switch and rolls back. The retry runner must stop at MaxAttempts and halt,
        // and each attempt must run the whole sequence again from the snapshot.
        FakeSteps steps = new FakeSteps().Script(LineMoveStep.Switch, (_, _) => throw new IOException("power cut"));
        List<TimeSpan> delays = [];
        LineMoveRetryRunner retrying = new(
            Runner(steps, new InMemoryAuditSink()),
            new LineMoveRetryPolicy(4, TimeSpan.FromMinutes(1), 2.0, TimeSpan.FromMinutes(3)),
            (wait, _) =>
            {
                delays.Add(wait);
                return Task.CompletedTask;
            });

        LineMoveRetryOutcome outcome = await retrying.RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(4, outcome.Attempts);
        Assert.True(outcome.Halted);
        Assert.Equal(4, steps.Ran.Count(s => s == LineMoveStep.Snapshot));
        Assert.Equal(4, steps.Ran.Count(s => s == LineMoveStep.RolledBack));
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)], delays);
    }

    [Fact]
    public async Task AFailedRollbackStopsTheRetryLoopAtOnce()
    {
        FakeSteps steps = new FakeSteps()
            .Fail(LineMoveStep.Verify, "red")
            .Script(LineMoveStep.RolledBack, (_, _) => throw new IOException("restore failed"));
        List<TimeSpan> delays = [];
        LineMoveRetryRunner retrying = new(Runner(steps, new InMemoryAuditSink()), LineMoveRetryPolicy.Default, (wait, _) =>
        {
            delays.Add(wait);
            return Task.CompletedTask;
        });

        LineMoveRetryOutcome outcome = await retrying.RunAsync(LineMoveTestData.Request(), CancellationToken.None);

        Assert.Equal(1, outcome.Attempts);
        Assert.False(outcome.Halted);
        Assert.Equal(LineMoveStatus.ManualRollbackRequired, outcome.Last.Status);
        Assert.Empty(delays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void RetryPolicyRefusesUnboundedAttemptCounts(int attempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineMoveRetryPolicy(attempts, TimeSpan.FromMinutes(1), 2.0, TimeSpan.FromHours(1)).Validated());
    }

    [Fact]
    public void RetryPolicyRefusesABackoffTheDelayApiCannotWaitFor()
    {
        // Task.Delay cannot wait longer than about 49 days, so a policy that asks for it would crash after the rollback.
        LineMoveRetryPolicy policy = new(3, TimeSpan.FromMinutes(1), 2.0, TimeSpan.FromDays(60));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Validated());
    }

    [Fact]
    public void BackoffNeverOverflowsOrGoesNegativeForAbsurdAttemptNumbers()
    {
        LineMoveRetryPolicy policy = new(10, TimeSpan.FromMinutes(10), 10.0, TimeSpan.FromHours(1));

        foreach (int attempt in new[] { 1, 2, 50, 400, 100_000, int.MaxValue })
        {
            TimeSpan wait = policy.BackoffAfter(attempt);
            Assert.True(wait >= TimeSpan.Zero && wait <= TimeSpan.FromHours(1));
        }
    }
}
