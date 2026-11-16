using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Evaluator;

/// <summary>
/// Table-driven checks of every row of the transition table (doc 46 section 8.2) and the state
/// descriptions of section 8.1. Each row names its doc section in the first column.
/// </summary>
public sealed class KodoEvaluatorTransitionTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds an observation from the table's columns. <c>AllRulesGreen</c> is derived, not given.
    /// </summary>
    private static KodoObservation Observe(
        bool criticalRed = false,
        bool criticalUnknown = false,
        bool integrity = false,
        bool crashLoop = false,
        int nonCriticalRedBeyondGrace = 0,
        bool amberBeyondGrace = false,
        bool sloWarningBurn = false,
        bool sloBreach = false,
        RecoveryStatus recovery = RecoveryStatus.Idle) => new()
        {
            CriticalRed = criticalRed,
            CriticalUnknown = criticalUnknown,
            IntegrityOrContractFailure = integrity,
            CrashLoop = crashLoop,
            NonCriticalRedBeyondGrace = nonCriticalRedBeyondGrace,
            NonCriticalAmberBeyondGrace = amberBeyondGrace,
            SloWarningBurn = sloWarningBurn,
            SloBreach = sloBreach,
            Recovery = recovery,
        };

    /// <summary>
    /// One row per case. <paramref name="windowPassed"/> says whether the previous all-green run had already
    /// lasted the stability window (true) or had only just begun (false). It matters only for green rows.
    /// </summary>
    [Theory]
    // Section 8.2, healthy -> degraded: non-critical red beyond its grace period.
    [InlineData("8.2 healthy->degraded (non-critical red)", KodoState.Healthy, false, false, false, false, 1, false, false, false, RecoveryStatus.Idle, false, KodoState.Degraded)]
    // Section 8.2, healthy -> degraded: sustained amber beyond its grace period.
    [InlineData("8.2 healthy->degraded (sustained amber)", KodoState.Healthy, false, false, false, false, 0, true, false, false, RecoveryStatus.Idle, false, KodoState.Degraded)]
    // Section 8.2, healthy -> degraded: SLO warning burn.
    [InlineData("8.2 healthy->degraded (SLO warning burn)", KodoState.Healthy, false, false, false, false, 0, false, true, false, RecoveryStatus.Idle, false, KodoState.Degraded)]
    // Section 8.1, degraded: a critical probe unknown within its unknown budget.
    [InlineData("8.1 degraded (critical unknown within budget)", KodoState.Healthy, false, true, false, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Degraded)]
    // Section 8.2, healthy -> failing: critical red.
    [InlineData("8.2 healthy->failing (critical red)", KodoState.Healthy, true, false, false, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.2, healthy -> failing: integrity or contract failure.
    [InlineData("8.2 healthy->failing (integrity or contract)", KodoState.Healthy, false, false, true, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.2, healthy -> failing: crash loop.
    [InlineData("8.2 healthy->failing (crash loop)", KodoState.Healthy, false, false, false, true, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.1, healthy: all green stays healthy.
    [InlineData("8.1 healthy (all green)", KodoState.Healthy, false, false, false, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Healthy)]
    // Section 8.2, degraded -> failing: any critical red.
    [InlineData("8.2 degraded->failing (critical red)", KodoState.Degraded, true, false, false, false, 1, false, true, false, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.2, degraded -> failing: a second non-critical red that breaches an SLO.
    [InlineData("8.2 degraded->failing (second red breaching SLO)", KodoState.Degraded, false, false, false, false, 2, false, true, true, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.2, degraded holds: one non-critical red breaching an SLO is not yet the second red.
    [InlineData("8.2 degraded holds (one red breaching SLO)", KodoState.Degraded, false, false, false, false, 1, false, true, true, RecoveryStatus.Idle, false, KodoState.Degraded)]
    // Section 8.2, degraded -> healthy: all rules green for the stability window.
    [InlineData("8.2 degraded->healthy (green for window)", KodoState.Degraded, false, false, false, false, 0, false, false, false, RecoveryStatus.Idle, true, KodoState.Healthy)]
    // Section 8.2, degraded holds: green but the stability window has not passed (hysteresis).
    [InlineData("8.2 degraded holds (green, window not passed)", KodoState.Degraded, false, false, false, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Degraded)]
    // Section 8.2, failing -> recovering: a recovery action starts.
    [InlineData("8.2 failing->recovering (action starts)", KodoState.Failing, true, false, false, false, 0, false, false, false, RecoveryStatus.Running, false, KodoState.Recovering)]
    // Section 8.2, failing holds with no action: a green sample does not flip failing to healthy.
    [InlineData("8.2 failing holds (green, no action)", KodoState.Failing, false, false, false, false, 0, false, false, false, RecoveryStatus.Idle, true, KodoState.Failing)]
    // Section 8.2, failing holds with no action while critical red persists.
    [InlineData("8.2 failing holds (critical red, no action)", KodoState.Failing, true, false, false, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.2, recovering: a running action continues and nothing new starts, even with red rules.
    [InlineData("8.2 recovering continues (action running, red)", KodoState.Recovering, true, false, false, false, 0, false, false, false, RecoveryStatus.Running, false, KodoState.Recovering)]
    // Section 8.2, recovering: a running action continues even when the window has passed (nothing new starts).
    [InlineData("8.2 recovering continues (action running, green)", KodoState.Recovering, false, false, false, false, 0, false, false, false, RecoveryStatus.Running, true, KodoState.Recovering)]
    // Section 8.2, recovering -> failing: the action failed or timed out.
    [InlineData("8.2 recovering->failing (action failed)", KodoState.Recovering, false, false, false, false, 0, false, false, false, RecoveryStatus.Failed, true, KodoState.Failing)]
    // Section 8.2, recovering -> failing: rules still red after the verify step (critical red).
    [InlineData("8.2 recovering->failing (still red after verify)", KodoState.Recovering, true, false, false, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.2, recovering -> failing: rules still red after the verify step (amber beyond grace).
    [InlineData("8.2 recovering->failing (amber after verify)", KodoState.Recovering, false, false, false, false, 0, true, false, false, RecoveryStatus.Idle, false, KodoState.Failing)]
    // Section 8.2, recovering -> healthy: stability window passed with all rules green.
    [InlineData("8.2 recovering->healthy (green for window)", KodoState.Recovering, false, false, false, false, 0, false, false, false, RecoveryStatus.Idle, true, KodoState.Healthy)]
    // Section 8.2, recovering holds: all green but the stability window has not passed (hysteresis).
    [InlineData("8.2 recovering holds (green, window not passed)", KodoState.Recovering, false, false, false, false, 0, false, false, false, RecoveryStatus.Idle, false, KodoState.Recovering)]
    public void TransitionMatchesTheTable(
        string docRow,
        KodoState from,
        bool criticalRed,
        bool criticalUnknown,
        bool integrity,
        bool crashLoop,
        int nonCriticalRedBeyondGrace,
        bool amberBeyondGrace,
        bool sloWarningBurn,
        bool sloBreach,
        RecoveryStatus recovery,
        bool windowPassed,
        KodoState expected)
    {
        KodoObservation observation = Observe(
            criticalRed, criticalUnknown, integrity, crashLoop,
            nonCriticalRedBeyondGrace, amberBeyondGrace, sloWarningBurn, sloBreach, recovery);

        // A green run that began one minute ago has not yet lasted the 5 minute window; one begun six minutes
        // ago has. Only green rows read this value.
        DateTimeOffset greenStart = windowPassed ? _now.AddMinutes(-6) : _now.AddMinutes(-1);
        KodoEvaluatorMemory previous = new(from, greenStart, [], false);

        KodoEvaluation result = KodoEvaluator.Evaluate(previous, observation, _now, new KodoEvaluatorSettings());

        Assert.True(expected == result.Underlying, $"{docRow}: expected {expected}, got {result.Underlying}");
        Assert.Equal(expected, result.State);
        Assert.NotEqual(KodoState.Frozen, result.Underlying);
    }

    [Fact]
    public void NonCriticalRedWithinGraceKeepsHealthy()
    {
        // Section 8.1 and 8.2: a non-critical red still inside its grace period neither degrades nor counts as green.
        KodoObservation observation = Observe() with { NonCriticalWithinGrace = true };
        Assert.False(observation.AllRulesGreen);

        KodoEvaluation result = KodoEvaluator.Evaluate(KodoEvaluator.Initial, observation, _now, new KodoEvaluatorSettings());

        Assert.Equal(KodoState.Healthy, result.Underlying);
        Assert.Null(result.Next.GreenSince);
    }

    [Fact]
    public void FirstGreenSampleOfAnEmptyRunDoesNotCompleteTheWindow()
    {
        // Section 8.2: recovering -> healthy needs the window to pass. The first green sample starts the run.
        KodoEvaluation result = KodoEvaluator.Evaluate(
            new KodoEvaluatorMemory(KodoState.Recovering, null, [], false),
            Observe(),
            _now,
            new KodoEvaluatorSettings());

        Assert.Equal(KodoState.Recovering, result.Underlying);
        Assert.Equal(_now, result.Next.GreenSince);
    }
}
