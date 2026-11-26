using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Testing;

namespace Bazaar.Kodo.Tests.Testing;

/// <summary>The fault harness drives the real evaluator, stream writer and restart backoff (doc 46 section 17.1).</summary>
public sealed class KodoFaultHarnessTests
{
    private static readonly TimeSpan _minute = TimeSpan.FromMinutes(1);

    private static FaultRun Run(TimeSpan duration, params ScriptedFault[] faults) =>
        new KodoFaultHarness().Run(new FaultScript(faults), duration);

    [Fact]
    public void NoFaultsStayHealthyWithNoEvents()
    {
        FaultRun run = Run(TimeSpan.FromMinutes(20));

        Assert.Equal([KodoState.Healthy], run.StateSequence);
        Assert.Empty(run.Events);
        Assert.Empty(new FaultExpectation { StateSequence = [KodoState.Healthy], FlapAlerts = 0 }.Check(run));
    }

    [Fact]
    public void CriticalProbeFailureGoesFailingAndStaysFailingWithoutARecovery()
    {
        // Section 8.2: failing -> healthy is not a direct transition, so the end of the fault does not clear it.
        FaultRun run = Run(TimeSpan.FromMinutes(30), ScriptedFault.ProbeFailure(_minute, _minute));

        Assert.Empty(new FaultExpectation { StateSequence = [KodoState.Healthy, KodoState.Failing], FlapAlerts = 0 }.Check(run));
        KodoStreamEventDto change = Assert.Single(run.Events);
        Assert.Equal("healthy", change.StateChange!.Previous);
        Assert.Equal("failing", change.StateChange.State);
    }

    [Fact]
    public void NonCriticalProbeFailureDegradesThenClearsAfterTheStabilityWindow()
    {
        FaultRun run = Run(TimeSpan.FromMinutes(30), ScriptedFault.ProbeFailure(_minute, _minute, critical: false));

        Assert.Empty(new FaultExpectation { StateSequence = [KodoState.Healthy, KodoState.Degraded, KodoState.Healthy] }.Check(run));
        FaultTick back = run.Ticks.First(t => t.Nominal > _minute && t.Evaluation.State == KodoState.Healthy);
        // Failure ends at 2 minutes, the first green sample is the stability window's start.
        Assert.True(back.Nominal >= TimeSpan.FromMinutes(2) + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void StaleDataIsUnknownNeverGreen()
    {
        FaultRun run = Run(TimeSpan.FromMinutes(30), ScriptedFault.StaleData(_minute, _minute));

        Assert.True(run.Ticks.Where(t => t.Nominal >= _minute && t.Nominal < 2 * _minute).All(t => t.Observation.CriticalUnknown && !t.Observation.AllRulesGreen));
        Assert.Empty(new FaultExpectation { StateSequence = [KodoState.Healthy, KodoState.Degraded, KodoState.Healthy] }.Check(run));
    }

    [Fact]
    public void ABackwardClockJumpDelaysRecoveryButNeverWedgesOrLosesEvents()
    {
        ScriptedFault failure = ScriptedFault.ProbeFailure(TimeSpan.Zero, TimeSpan.FromSeconds(30), critical: false);
        FaultRun steady = Run(TimeSpan.FromMinutes(30), failure);
        FaultRun jumped = Run(TimeSpan.FromMinutes(30), failure, ScriptedFault.ClockJump(2 * _minute, -TimeSpan.FromHours(1)));

        Assert.Equal(KodoState.Healthy, steady.Ticks[^1].Evaluation.State);
        Assert.Equal(KodoState.Degraded, jumped.Ticks[^1].Evaluation.State);
        Assert.Empty(new FaultExpectation().Check(jumped));
        Assert.Equal(TimeSpan.FromMinutes(30), jumped.Ticks[^1].Nominal);
        Assert.True(jumped.Ticks[^1].Wall < steady.Ticks[^1].Wall);
    }

    [Fact]
    public void AForwardClockJumpCanSatisfyTheStabilityWindowEarly()
    {
        // Observed behaviour, recorded in 17.5: the evaluator trusts the time it is given.
        ScriptedFault failure = ScriptedFault.ProbeFailure(TimeSpan.Zero, TimeSpan.FromSeconds(30), critical: false);
        FaultRun run = Run(TimeSpan.FromMinutes(10), failure, ScriptedFault.ClockJump(2 * _minute, TimeSpan.FromHours(1)));

        FaultTick healthy = run.Ticks.First(t => t.Evaluation.State == KodoState.Healthy && t.Nominal > TimeSpan.Zero);
        Assert.True(healthy.Nominal < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void FourRestartsAreNotACrashLoopAndFiveAre()
    {
        FaultRun four = Run(10 * _minute, ScriptedFault.RestartStorm(_minute, 4));
        FaultRun five = Run(10 * _minute, ScriptedFault.RestartStorm(_minute, 5));

        Assert.Equal([KodoState.Healthy], four.StateSequence);
        Assert.Equal([KodoState.Healthy, KodoState.Failing], five.StateSequence);
        Assert.Contains(five.Ticks, t => t.Observation.CrashLoop);
        Assert.Empty(new FaultExpectation().Check(five));
    }

    [Fact]
    public void RestartStormUsesTheShippedBackoffSchedule()
    {
        FaultRun run = Run(2 * _minute, ScriptedFault.RestartStorm(TimeSpan.Zero, 7));

        Assert.Equal(
            [1d, 2d, 5d, 15d, 60d, 300d, 300d],
            run.RestartDecisions.Select(d => d.Delay.TotalSeconds));
    }

    [Fact]
    public void RepeatedShortFailuresRaiseOneGroupedFlapAlertAndHoldDegraded()
    {
        KodoFaultHarness harness = new(new KodoEvaluatorSettings { StabilityWindow = TimeSpan.FromSeconds(60) });
        ScriptedFault[] faults = Enumerable.Range(0, 6)
            .Select(i => ScriptedFault.ProbeFailure(TimeSpan.FromSeconds(100 * i), TimeSpan.FromSeconds(20), critical: false))
            .ToArray();

        FaultRun run = harness.Run(new FaultScript(faults), TimeSpan.FromSeconds(900));

        Assert.Equal(1, run.FlapAlertCount);
        Assert.Contains(run.Ticks, t => t.Evaluation.Flapping);
        Assert.Empty(new FaultExpectation().Check(run));
    }

    [Fact]
    public void ARunIsDeterministic()
    {
        ScriptedFault[] faults =
        [
            ScriptedFault.ProbeFailure(_minute, _minute, critical: false),
            ScriptedFault.ClockJump(4 * _minute, -TimeSpan.FromMinutes(2)),
            ScriptedFault.RestartStorm(6 * _minute, 5),
        ];

        FaultRun a = Run(30 * _minute, faults);
        FaultRun b = Run(30 * _minute, faults);

        Assert.Equal(a.StateSequence, b.StateSequence);
        Assert.Equal(a.Events, b.Events);
        Assert.Equal(a.RestartDecisions, b.RestartDecisions);
    }

    [Fact]
    public void ExpectationReportsEveryMismatch()
    {
        FaultRun run = Run(5 * _minute, ScriptedFault.ProbeFailure(_minute, _minute));

        IReadOnlyList<string> failures = new FaultExpectation { StateSequence = [KodoState.Healthy], FlapAlerts = 2 }.Check(run);

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("State sequence", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Flap alerts", StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptSortsFaultsByTimeKeepingTheOrderOfEqualTimes()
    {
        ScriptedFault late = ScriptedFault.StaleData(_minute, _minute);
        ScriptedFault first = ScriptedFault.ProbeFailure(TimeSpan.Zero, _minute);
        ScriptedFault second = ScriptedFault.RestartStorm(TimeSpan.Zero, 1);

        FaultScript script = new([late, first, second]);

        Assert.Equal([first, second, late], script.Faults);
    }

    [Fact]
    public void BadFaultsAndHarnessArgumentsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptedFault.ProbeFailure(-_minute, _minute));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptedFault.StaleData(TimeSpan.Zero, -_minute));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptedFault.RestartStorm(TimeSpan.Zero, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KodoFaultHarness(tick: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KodoFaultHarness(settings: new KodoEvaluatorSettings { StabilityWindow = TimeSpan.FromSeconds(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KodoFaultHarness().Run(new FaultScript([]), -_minute));
    }

    [Fact]
    public void ABrokenAuditTrailIsReported()
    {
        FaultRun run = Run(5 * _minute, ScriptedFault.ProbeFailure(_minute, _minute));
        FaultRun lossy = run with { Events = [] };

        IReadOnlyList<string> failures = new FaultExpectation().Check(lossy);

        Assert.Contains(failures, f => f.Contains("state-changed events", StringComparison.Ordinal));
    }

    [Fact]
    public void AGappedStreamIsReported()
    {
        FaultRun run = Run(5 * _minute, ScriptedFault.ProbeFailure(_minute, _minute));
        KodoStreamEventDto only = run.Events[0];
        FaultRun gapped = run with { Events = [only with { Revision = 3 }] };

        Assert.False(gapped.EventsGapFree);
        Assert.Contains(new FaultExpectation().Check(gapped), f => f.Contains("gap", StringComparison.Ordinal));
    }
}
