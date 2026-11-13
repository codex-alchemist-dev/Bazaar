using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Evaluator;

/// <summary>
/// Hysteresis, flap detection, incident freezing and settings floors (doc 46 sections 8.1, 8.2 and 20).
/// </summary>
public sealed class KodoEvaluatorHysteresisTests
{
    private static readonly DateTimeOffset _Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly KodoEvaluatorSettings _Settings = new();

    private static readonly KodoObservation _Green = new();
    private static readonly KodoObservation _Amber = new() { NonCriticalAmberBeyondGrace = true };
    private static readonly KodoObservation _CriticalRed = new() { CriticalRed = true };
    private static readonly KodoObservation _RecoveryRunning = new() { CriticalRed = true, Recovery = RecoveryStatus.Running };
    private static readonly KodoObservation _RecoveryFailed = new() { CriticalRed = true, Recovery = RecoveryStatus.Failed };

    /// <summary>Evaluates one sample and carries the memory forward, as the caller does.</summary>
    private static KodoEvaluation Step(ref KodoEvaluatorMemory memory, KodoObservation observation, DateTimeOffset at)
    {
        KodoEvaluation result = KodoEvaluator.Evaluate(memory, observation, at, _Settings);
        memory = result.Next;
        return result;
    }

    [Fact]
    public void OneGreenSampleDoesNotFlipDegradedToHealthy()
    {
        // Section 8.2, hysteresis: degraded -> healthy needs all rules green for the whole stability window.
        KodoEvaluatorMemory memory = new(KodoState.Degraded, null, [], false);

        Assert.Equal(KodoState.Degraded, Step(ref memory, _Green, _Start).Underlying);
        Assert.Equal(KodoState.Degraded, Step(ref memory, _Green, _Start.AddMinutes(4)).Underlying);
        Assert.Equal(KodoState.Healthy, Step(ref memory, _Green, _Start.AddMinutes(5)).Underlying);
    }

    [Fact]
    public void AnyNonGreenSampleRestartsTheGreenRun()
    {
        // Section 8.2, hysteresis: a sample that is not all green resets the run, so the window starts again.
        KodoEvaluatorMemory memory = new(KodoState.Degraded, null, [], false);

        Step(ref memory, _Green, _Start);
        Step(ref memory, _Amber, _Start.AddMinutes(2));
        Step(ref memory, _Green, _Start.AddMinutes(3));

        Assert.Equal(KodoState.Degraded, Step(ref memory, _Green, _Start.AddMinutes(7)).Underlying);
        Assert.Equal(KodoState.Healthy, Step(ref memory, _Green, _Start.AddMinutes(8)).Underlying);
    }

    [Fact]
    public void FailingDoesNotFlipToHealthyOnGreenSamplesWithoutRecovery()
    {
        // Section 8.2: failing -> healthy is not used directly. A green sample, however long, is not a recovery.
        KodoEvaluatorMemory memory = new(KodoState.Failing, null, [], false);

        Assert.Equal(KodoState.Failing, Step(ref memory, _Green, _Start).Underlying);
        Assert.Equal(KodoState.Failing, Step(ref memory, _Green, _Start.AddMinutes(10)).Underlying);
    }

    [Fact]
    public void RecoveringNeedsTheWholeWindowBeforeHealthy()
    {
        // Section 8.2: recovering -> healthy needs the stability window with all rules green.
        KodoEvaluatorMemory memory = new(KodoState.Recovering, null, [], false);

        Assert.Equal(KodoState.Recovering, Step(ref memory, _Green, _Start).Underlying);
        Assert.Equal(KodoState.Recovering, Step(ref memory, _Green, _Start.AddMinutes(4)).Underlying);
        Assert.Equal(KodoState.Healthy, Step(ref memory, _Green, _Start.AddMinutes(5)).Underlying);
    }

    [Fact]
    public void FlapDetectedOnTheSeventhTransitionInTheWindow()
    {
        // Section 8.2: more than 6 transitions in 30 minutes flags flapping. The alert is raised once.
        KodoEvaluatorMemory memory = KodoEvaluator.Initial;
        var flags = new List<bool>();
        var alerts = new List<bool>();

        // Failing, then recovering and failing again, one minute apart: each step is a transition.
        KodoObservation[] steps =
        [
            _CriticalRed,
            _RecoveryRunning,
            _RecoveryFailed,
            _RecoveryRunning,
            _RecoveryFailed,
            _RecoveryRunning,
            _RecoveryFailed,
            _RecoveryRunning,
        ];

        for (int i = 0; i < steps.Length; i++)
        {
            KodoEvaluation result = Step(ref memory, steps[i], _Start.AddMinutes(i));
            flags.Add(result.Flapping);
            alerts.Add(result.RaiseFlapAlert);
        }

        // Transitions 1 to 6 are not flapping. The 7th (index 6) is, and it raises the one alert.
        Assert.DoesNotContain(true, flags.Take(6));
        Assert.True(flags[6]);
        Assert.True(alerts[6]);
        Assert.False(alerts[7]);
        Assert.True(flags[7]);
    }

    [Fact]
    public void FlapHoldsHealthyAndDegradedAtDegradedUntilTheWindowClears()
    {
        // Section 8.2: flap detection forces degraded. The state is held there until the transitions leave the window.
        KodoEvaluatorMemory memory = KodoEvaluator.Initial;
        var schedule = new (int Minute, KodoObservation Observation)[]
        {
            (0, _Amber), (1, _Green), (6, _Green), (7, _Amber), (8, _Green), (13, _Green), (14, _Amber),
            (15, _Green), (20, _Green), (21, _Amber), // seventh transition: flapping starts here
        };
        foreach ((int minute, KodoObservation observation) in schedule)
        {
            Step(ref memory, observation, _Start.AddMinutes(minute));
        }

        Assert.True(memory.Flapping);

        // Healthy would be eligible here (green since minute 22, for 5 minutes), but flapping holds it degraded.
        KodoEvaluation held = Step(ref memory, _Green, _Start.AddMinutes(22));
        KodoEvaluation heldLater = Step(ref memory, _Green, _Start.AddMinutes(27));
        Assert.Equal(KodoState.Degraded, held.Underlying);
        Assert.Equal(KodoState.Degraded, heldLater.Underlying);
        Assert.True(heldLater.Flapping);
        Assert.False(heldLater.RaiseFlapAlert);

        // Once every transition has left the 30 minute window, the normal rules apply again.
        KodoEvaluation released = Step(ref memory, _Green, _Start.AddMinutes(52));
        Assert.Equal(KodoState.Healthy, released.Underlying);
        Assert.False(released.Flapping);
    }

    [Fact]
    public void FlapDoesNotHideCriticalRed()
    {
        // Section 8.2 forces degraded only over healthy or degraded. A critical red stays failing.
        KodoEvaluatorMemory memory = new(KodoState.Healthy, null, [.. Enumerable.Range(0, 6).Select(i => _Start.AddMinutes(i))], true);

        KodoEvaluation result = Step(ref memory, _CriticalRed, _Start.AddMinutes(7));

        Assert.True(result.Flapping);
        Assert.Equal(KodoState.Failing, result.Underlying);
    }

    [Fact]
    public void IncidentModeShowsFrozenBesideTheUnderlyingState()
    {
        // Section 8.1 and 16.3: frozen is shown beside the underlying health, which is still computed.
        KodoEvaluatorMemory memory = new(KodoState.Failing, null, [], false);
        KodoObservation incident = _CriticalRed with { IncidentActive = true };

        KodoEvaluation result = Step(ref memory, incident, _Start);

        Assert.Equal(KodoState.Frozen, result.State);
        Assert.Equal(KodoState.Failing, result.Underlying);
    }

    [Fact]
    public void ExitingIncidentModeShowsTheReEvaluatedState()
    {
        // Section 8.2: frozen -> previous or re-evaluated state once incident mode exits.
        KodoEvaluatorMemory memory = new(KodoState.Failing, null, [], false);
        Step(ref memory, _CriticalRed with { IncidentActive = true }, _Start);

        KodoEvaluation result = Step(ref memory, _CriticalRed, _Start.AddMinutes(1));

        Assert.Equal(KodoState.Failing, result.State);
        Assert.Equal(KodoState.Failing, result.Underlying);
    }

    [Fact]
    public void EvaluationIsDeterministicForTheSameTime()
    {
        // The evaluator reads no clock, so the same inputs always give the same result.
        KodoEvaluatorMemory memory = new(KodoState.Degraded, _Start, [], false);

        KodoEvaluation first = KodoEvaluator.Evaluate(memory, _Green, _Start.AddMinutes(5), _Settings);
        KodoEvaluation second = KodoEvaluator.Evaluate(memory, _Green, _Start.AddMinutes(5), _Settings);

        Assert.Equal(first.State, second.State);
        Assert.Equal(first.Underlying, second.Underlying);
        Assert.Equal(first.Next.GreenSince, second.Next.GreenSince);
        Assert.Equal(first.Next.Transitions, second.Next.Transitions);
    }

    [Fact]
    public void StabilityWindowBelowItsFloorIsRejected()
    {
        // Section 20: kodo.state.stability_window_s has a floor of 60 seconds.
        var settings = new KodoEvaluatorSettings { StabilityWindow = TimeSpan.FromSeconds(30) };

        Assert.Throws<ArgumentOutOfRangeException>(() => KodoEvaluator.Evaluate(KodoEvaluator.Initial, _Green, _Start, settings));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FlapLimitBelowOneIsRejected(int limit)
    {
        // Section 20: kodo.state.flap_limit must allow at least one transition.
        var settings = new KodoEvaluatorSettings { FlapLimit = limit };

        Assert.Throws<ArgumentOutOfRangeException>(() => KodoEvaluator.Evaluate(KodoEvaluator.Initial, _Green, _Start, settings));
    }

    [Fact]
    public void DefaultSettingsMatchTheDesignDocument()
    {
        // Section 8.2 and 20: stability window 5 minutes, flap limit 6 in 30 minutes.
        KodoEvaluatorSettings defaults = new();

        Assert.Equal(TimeSpan.FromMinutes(5), defaults.StabilityWindow);
        Assert.Equal(6, defaults.FlapLimit);
        Assert.Equal(TimeSpan.FromMinutes(30), defaults.FlapWindow);
    }
}
