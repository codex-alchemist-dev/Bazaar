using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.SelfChecks;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.SelfChecks;

public sealed class SelfCheckTests
{
    private static readonly DateTimeOffset _anchor = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

    private static SelfCheckDefinition Def(SelfCheckKind kind = SelfCheckKind.RestoreBackup, int days = 7, bool enabled = true) =>
        new($"selfcheck.{kind}", kind, new SelfCheckSchedule(_anchor, TimeSpan.FromDays(days)), TimeSpan.FromMinutes(5), TimeSpan.FromHours(12), enabled);

    private sealed class StubRunner(SelfCheckKind kind, Func<CancellationToken, Task<SelfCheckRunnerResult>> body) : ISelfCheckRunner
    {
        public int Calls { get; private set; }

        public SelfCheckKind Kind { get; } = kind;

        public Task<SelfCheckRunnerResult> RunAsync(SelfCheckContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return body(cancellationToken);
        }
    }

    private static StubRunner Returns(SelfCheckKind kind, SelfCheckRunnerOutcome outcome, string cause = "ok", int items = 3) =>
        new(kind, _ => Task.FromResult(new SelfCheckRunnerResult(outcome, cause, items)));

    [Fact]
    public void ScheduleIsAnIntervalFromTheAnchor()
    {
        SelfCheckSchedule s = new(_anchor, TimeSpan.FromDays(1));
        Assert.Equal(_anchor, s.NextDue(_anchor.AddDays(-5)));
        Assert.Equal(_anchor.AddDays(1), s.NextDue(_anchor));
        Assert.Equal(_anchor.AddDays(3), s.NextDue(_anchor.AddDays(2).AddHours(1)));
        Assert.Null(s.PreviousDue(_anchor.AddTicks(-1)));
        Assert.Equal(_anchor.AddDays(2), s.PreviousDue(_anchor.AddDays(2).AddHours(5)));
    }

    [Fact]
    public void ScheduleAndDefinitionRefuseBadValues()
    {
        Assert.Throws<ArgumentException>(() => new SelfCheckSchedule(_anchor.ToOffset(TimeSpan.FromHours(2)), TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentException>(() => new SelfCheckSchedule(_anchor, TimeSpan.FromMinutes(5)));
        SelfCheckSchedule ok = new(_anchor, TimeSpan.FromDays(1));
        Assert.Throws<ArgumentException>(() => new SelfCheckDefinition("bad id", SelfCheckKind.ChecksumSweep, ok, TimeSpan.FromMinutes(1), TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new SelfCheckDefinition("x", SelfCheckKind.ChecksumSweep, ok, TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void DefaultsFollowSection172AndDisableTheDrillWithoutStandby()
    {
        IReadOnlyList<SelfCheckDefinition> defaults = SelfCheckDefinition.Defaults(_anchor, standbyConfigured: false);
        Assert.Equal([1, 7, 30, 90], defaults.Select(d => (int)d.Schedule.Interval.TotalDays));
        Assert.Equal([true, true, true, false], defaults.Select(d => d.Enabled));
        Assert.True(SelfCheckDefinition.Defaults(_anchor, true)[3].Enabled);
    }

    [Fact]
    public void ResultIsGreenOnlyForARealPass()
    {
        SelfCheckDefinition d = Def();
        DateTimeOffset t = _anchor;
        Assert.Equal(SelfCheckStatus.Green, SelfCheckResult.FromRunner(d, t, t, t, new(SelfCheckRunnerOutcome.Passed, "ok", 4)).Status);
        SelfCheckResult empty = SelfCheckResult.FromRunner(d, t, t, t, new(SelfCheckRunnerOutcome.Passed, "ok", 0));
        Assert.Equal(SelfCheckOutcome.Skipped, empty.Outcome);
        Assert.Equal(SelfCheckCauses.NothingChecked, empty.Cause);
        Assert.Equal(SelfCheckStatus.Amber, empty.Status);
        Assert.Equal(SelfCheckStatus.Red, SelfCheckResult.FromRunner(d, t, t, t, new(SelfCheckRunnerOutcome.Failed, "restore_failed", 1)).Status);
        Assert.Equal(SelfCheckStatus.Amber, SelfCheckResult.FromRunner(d, t, t, t, new(SelfCheckRunnerOutcome.Skipped, "precondition_missing", 0)).Status);
        Assert.Equal(SelfCheckStatus.Amber, SelfCheckResult.TimedOut(d, t, t, t).Status);
        Assert.Equal(SelfCheckStatus.Amber, SelfCheckResult.Faulted(d, t, t, t).Status);
        Assert.Equal(SelfCheckStatus.Amber, SelfCheckResult.FromRunner(d, t, t, t, null).Status);
    }

    [Fact]
    public void ACauseThatIsNotCodeShapedIsReplaced()
    {
        SelfCheckDefinition d = Def();
        SelfCheckResult r = SelfCheckResult.FromRunner(d, _anchor, _anchor, _anchor, new(SelfCheckRunnerOutcome.Failed, "Bad Cause: /secret/path", 1));
        Assert.Equal(SelfCheckCauses.InvalidCause, r.Cause);
        Assert.Equal(SelfCheckOutcome.Failed, r.Outcome);
    }

    [Fact]
    public void HistoryIsBoundedPerCheckAndInTotal()
    {
        SelfCheckHistory h = new(3);
        SelfCheckDefinition d = Def();
        for (int i = 0; i < 5; i++)
        {
            h.Add(SelfCheckResult.Skipped(d, _anchor, _anchor.AddDays(i), SelfCheckCauses.NoRunner));
        }

        Assert.Equal(3, h.Recent(d.Id).Count);
        Assert.Equal(_anchor.AddDays(4), h.Latest(d.Id)!.StartedAt);
        Assert.Equal(_anchor.AddDays(2), h.Recent(d.Id)[0].StartedAt);

        SelfCheckHistory many = new();
        for (int i = 0; i < SelfCheckHistory.MaxDefinitions; i++)
        {
            Assert.True(many.Add(SelfCheckResult.Skipped(new($"c{i}", SelfCheckKind.ChecksumSweep, d.Schedule, TimeSpan.FromMinutes(1), TimeSpan.Zero), _anchor, _anchor, "x")));
        }

        Assert.False(many.Add(SelfCheckResult.Skipped(new("extra", SelfCheckKind.ChecksumSweep, d.Schedule, TimeSpan.FromMinutes(1), TimeSpan.Zero), _anchor, _anchor, "x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SelfCheckHistory(0));
    }

    [Fact]
    public async Task SchedulerRunsADueCheckOnceAndNotAgainUntilTheNextSlot()
    {
        FakeTimeProvider time = new(_anchor.AddDays(7).AddHours(1));
        SelfCheckHistory history = new();
        StubRunner runner = Returns(SelfCheckKind.RestoreBackup, SelfCheckRunnerOutcome.Passed);
        SelfCheckScheduler scheduler = new([runner], history, time);
        SelfCheckDefinition[] defs = [Def()];

        SelfCheckBatch first = await scheduler.RunDueAsync(defs, SelfCheckScheduleState.Empty, CancellationToken.None);
        Assert.Single(first.Results);
        Assert.Equal(_anchor.AddDays(7), first.Results[0].Slot);
        Assert.Equal(SelfCheckStatus.Green, first.Results[0].Status);

        SelfCheckBatch again = await scheduler.RunDueAsync(defs, first.State, CancellationToken.None);
        Assert.Empty(again.Results);
        Assert.Equal(1, runner.Calls);

        time.Advance(TimeSpan.FromDays(21));
        SelfCheckBatch later = await scheduler.RunDueAsync(defs, again.State, CancellationToken.None);
        Assert.Single(later.Results);
        Assert.Equal(_anchor.AddDays(28), later.Results[0].Slot);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task SchedulerSkipsDisabledChecksAndChecksBeforeTheirAnchor()
    {
        FakeTimeProvider time = new(_anchor.AddDays(-1));
        StubRunner runner = Returns(SelfCheckKind.RestoreBackup, SelfCheckRunnerOutcome.Passed);
        SelfCheckScheduler scheduler = new([runner], new SelfCheckHistory(), time);

        SelfCheckBatch early = await scheduler.RunDueAsync([Def()], SelfCheckScheduleState.Empty, CancellationToken.None);
        Assert.Empty(early.Results);

        time.SetUtcNow(_anchor.AddDays(30));
        SelfCheckBatch off = await scheduler.RunDueAsync([Def(enabled: false)], SelfCheckScheduleState.Empty, CancellationToken.None);
        Assert.Empty(off.Results);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task AMissingRunnerIsASkipNotAPass()
    {
        FakeTimeProvider time = new(_anchor.AddHours(1));
        SelfCheckScheduler scheduler = new([], new SelfCheckHistory(), time);
        SelfCheckBatch batch = await scheduler.RunDueAsync([Def()], SelfCheckScheduleState.Empty, CancellationToken.None);
        Assert.Equal(SelfCheckCauses.NoRunner, batch.Results[0].Cause);
        Assert.Equal(SelfCheckStatus.Amber, batch.Results[0].Status);
    }

    [Fact]
    public async Task AThrowingRunnerIsContained()
    {
        FakeTimeProvider time = new(_anchor.AddHours(1));
        StubRunner boom = new(SelfCheckKind.RestoreBackup, _ => throw new InvalidOperationException("secret detail"));
        StubRunner async = new(SelfCheckKind.ChecksumSweep, _ => Task.FromException<SelfCheckRunnerResult>(new IOException("secret")));
        SelfCheckScheduler scheduler = new([boom, async], new SelfCheckHistory(), time);

        SelfCheckBatch batch = await scheduler.RunDueAsync([Def(), Def(SelfCheckKind.ChecksumSweep, 1)], SelfCheckScheduleState.Empty, CancellationToken.None);

        Assert.False(batch.Faulted);
        Assert.All(batch.Results, r => Assert.Equal(SelfCheckOutcome.Faulted, r.Outcome));
        Assert.All(batch.Results, r => Assert.Equal(SelfCheckCauses.RunnerException, r.Cause));
    }

    [Fact]
    public async Task AHangingRunnerTimesOutAndIsNeverGreen()
    {
        FakeTimeProvider time = new(_anchor.AddHours(1));
        TaskCompletionSource<SelfCheckRunnerResult> never = new();
        StubRunner hang = new(SelfCheckKind.RestoreBackup, _ => never.Task);
        SelfCheckScheduler scheduler = new([hang], new SelfCheckHistory(), time);

        Task<SelfCheckBatch> pending = scheduler.RunDueAsync([Def()], SelfCheckScheduleState.Empty, CancellationToken.None);
        while (hang.Calls == 0)
        {
            await Task.Yield();
        }

        // The timer may not exist yet when the runner is first called, so keep moving time until the run ends.
        while (!pending.IsCompleted)
        {
            time.Advance(TimeSpan.FromMinutes(6));
            await Task.Yield();
        }

        SelfCheckBatch batch = await pending;

        Assert.Equal(SelfCheckOutcome.TimedOut, batch.Results[0].Outcome);
        Assert.Equal(SelfCheckStatus.Amber, batch.Results[0].Status);
    }

    [Fact]
    public async Task OnlyTheCallersCancellationPropagates()
    {
        FakeTimeProvider time = new(_anchor.AddHours(1));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        SelfCheckScheduler scheduler = new([Returns(SelfCheckKind.RestoreBackup, SelfCheckRunnerOutcome.Passed)], new SelfCheckHistory(), time);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunDueAsync([Def()], SelfCheckScheduleState.Empty, cts.Token));

        // A runner that cancels on its own is a fault, not a cancellation of the host.
        StubRunner self = new(SelfCheckKind.RestoreBackup, _ => throw new OperationCanceledException());
        SelfCheckBatch batch = await new SelfCheckScheduler([self], new SelfCheckHistory(), time).RunDueAsync([Def()], SelfCheckScheduleState.Empty, CancellationToken.None);
        Assert.Equal(SelfCheckOutcome.Faulted, batch.Results[0].Outcome);
    }

    [Fact]
    public async Task AnOverlappingCallReturnsBusy()
    {
        FakeTimeProvider time = new(_anchor.AddHours(1));
        TaskCompletionSource<SelfCheckRunnerResult> gate = new();
        StubRunner slow = new(SelfCheckKind.RestoreBackup, _ => gate.Task);
        SelfCheckScheduler scheduler = new([slow], new SelfCheckHistory(), time);

        Task<SelfCheckBatch> first = scheduler.RunDueAsync([Def()], SelfCheckScheduleState.Empty, CancellationToken.None);
        while (slow.Calls == 0)
        {
            await Task.Yield();
        }

        SelfCheckBatch second = await scheduler.RunDueAsync([Def()], SelfCheckScheduleState.Empty, CancellationToken.None);
        Assert.True(second.Busy);
        gate.SetResult(new(SelfCheckRunnerOutcome.Passed, "ok", 1));
        Assert.False((await first).Busy);
    }

    private static SelfCheckHistory With(SelfCheckDefinition d, params (SelfCheckOutcome Outcome, DateTimeOffset At)[] runs)
    {
        SelfCheckHistory h = new();
        foreach ((SelfCheckOutcome outcome, DateTimeOffset at) in runs)
        {
            SelfCheckRunnerResult? answer = outcome switch
            {
                SelfCheckOutcome.Passed => new(SelfCheckRunnerOutcome.Passed, "ok", 2),
                SelfCheckOutcome.Failed => new(SelfCheckRunnerOutcome.Failed, "restore_failed", 2),
                _ => new(SelfCheckRunnerOutcome.Skipped, "precondition_missing", 0),
            };
            h.Add(SelfCheckResult.FromRunner(d, at, at, at, answer));
        }

        return h;
    }

    [Fact]
    public void RecentPassIsGreenAndAllRulesGreen()
    {
        SelfCheckDefinition d = Def();
        SelfCheckAssessment a = SelfCheckObservationMapper.Assess([d], With(d, (SelfCheckOutcome.Passed, _anchor.AddDays(7))), _anchor.AddDays(8));
        Assert.True(a.Observation.AllRulesGreen);
        Assert.Equal(SelfCheckStatus.Green, a.Lines[0].Status);
    }

    [Fact]
    public void AStalePassNeverRunAndSkipsAreNeverGreen()
    {
        SelfCheckDefinition d = Def();
        SelfCheckAssessment stale = SelfCheckObservationMapper.Assess([d], With(d, (SelfCheckOutcome.Passed, _anchor)), _anchor.AddDays(9));
        Assert.True(stale.Observation.NonCriticalAmberBeyondGrace);
        Assert.Equal(SelfCheckCauses.Stale, stale.Lines[0].Cause);

        SelfCheckAssessment never = SelfCheckObservationMapper.Assess([d], new SelfCheckHistory(), _anchor.AddDays(2));
        Assert.True(never.Observation.NonCriticalAmberBeyondGrace);
        Assert.False(never.Observation.AllRulesGreen);

        SelfCheckAssessment fresh = SelfCheckObservationMapper.Assess([d], new SelfCheckHistory(), _anchor.AddHours(1));
        Assert.True(fresh.Observation.NonCriticalWithinGrace);
        Assert.False(fresh.Observation.AllRulesGreen);

        SelfCheckAssessment skipped = SelfCheckObservationMapper.Assess([d], With(d, (SelfCheckOutcome.Skipped, _anchor.AddDays(7))), _anchor.AddDays(7).AddHours(1));
        Assert.True(skipped.Observation.NonCriticalWithinGrace);
        Assert.False(skipped.Observation.AllRulesGreen);

        SelfCheckAssessment skippedLong = SelfCheckObservationMapper.Assess([d], With(d, (SelfCheckOutcome.Skipped, _anchor.AddDays(7))), _anchor.AddDays(8));
        Assert.True(skippedLong.Observation.NonCriticalAmberBeyondGrace);
    }

    [Fact]
    public void AFailedRestoreEscalatesPastItsGraceFromTheFirstFailure()
    {
        SelfCheckDefinition d = Def();
        SelfCheckHistory h = With(d, (SelfCheckOutcome.Passed, _anchor), (SelfCheckOutcome.Failed, _anchor.AddDays(7)), (SelfCheckOutcome.Failed, _anchor.AddDays(7).AddHours(10)));

        SelfCheckAssessment early = SelfCheckObservationMapper.Assess([d], h, _anchor.AddDays(7).AddHours(11));
        Assert.True(early.Observation.NonCriticalWithinGrace);
        Assert.Equal(0, early.Observation.NonCriticalRedBeyondGrace);

        SelfCheckAssessment late = SelfCheckObservationMapper.Assess([d], h, _anchor.AddDays(7).AddHours(30));
        Assert.Equal(1, late.Observation.NonCriticalRedBeyondGrace);
        Assert.Equal(SelfCheckStatus.Red, late.Lines[0].Status);
    }

    [Fact]
    public void AChecksumMismatchIsAnIntegrityFindingAtOnce()
    {
        SelfCheckDefinition d = Def(SelfCheckKind.ChecksumSweep, 1);
        SelfCheckAssessment a = SelfCheckObservationMapper.Assess([d], With(d, (SelfCheckOutcome.Failed, _anchor)), _anchor.AddMinutes(1));
        Assert.True(a.Observation.IntegrityOrContractFailure);
        KodoEvaluation result = KodoEvaluator.Evaluate(KodoEvaluator.Initial, a.Observation, _anchor, new KodoEvaluatorSettings());
        Assert.NotEqual(KodoState.Healthy, result.State);
    }

    [Fact]
    public void DisabledChecksAreLeftOutAndMergeOnlyRaises()
    {
        SelfCheckDefinition off = Def(SelfCheckKind.FailoverDrill, 90, enabled: false);
        SelfCheckAssessment a = SelfCheckObservationMapper.Assess([off], new SelfCheckHistory(), _anchor.AddDays(200));
        Assert.Empty(a.Lines);
        Assert.True(a.Observation.AllRulesGreen);

        KodoObservation merged = SelfCheckObservationMapper.Merge(
            new KodoObservation { CriticalRed = true, NonCriticalRedBeyondGrace = 1 },
            new KodoObservation { NonCriticalRedBeyondGrace = 2, NonCriticalWithinGrace = true });
        Assert.True(merged.CriticalRed);
        Assert.Equal(3, merged.NonCriticalRedBeyondGrace);
        Assert.True(merged.NonCriticalWithinGrace);
    }
}
