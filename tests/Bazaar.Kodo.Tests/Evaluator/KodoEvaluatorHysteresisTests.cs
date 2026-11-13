using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Evaluator;

/// <summary>
/// Hysteresis, flap detection, incident freezing and settings floors (doc 46 sections 8.1, 8.2 and 20).
/// </summary>
public sealed class KodoEvaluatorHysteresisTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly KodoEvaluatorSettings _settings = new();

    private static readonly KodoObservation _green = new();
    private static readonly KodoObservation _amber = new() { NonCriticalAmberBeyondGrace = true };
    private static readonly KodoObservation _criticalRed = new() { CriticalRed = true };
    private static readonly KodoObservation _recoveryRunning = new() { CriticalRed = true, Recovery = RecoveryStatus.Running };
    private static readonly KodoObservation _recoveryFailed = new() { CriticalRed = true, Recovery = RecoveryStatus.Failed };

    /// <summary>Evaluates one sample and carries the memory forward, as the caller does.</summary>
    private static KodoEvaluation Step(ref KodoEvaluatorMemory memory, KodoObservation observation, DateTimeOffset at)
    {
        KodoEvaluation result = KodoEvaluator.Evaluate(memory, observation, at, _settings);
        memory = result.Next;
        return result;
    }

    [Fact]
    public void OneGreenSampleDoesNotFlipDegradedToHealthy()
    {
        // Section 8.2, hysteresis: degraded -> healthy needs all rules green for the whole stability window.
        KodoEvaluatorMemory memory = new(KodoState.Degraded, null, [], false);

        Assert.Equal(KodoState.Degraded, Step(ref memory, _green, _start).Underlying);
        Assert.Equal(KodoState.Degraded, Step(ref memory, _green, _start.AddMinutes(4)).Underlying);
        Assert.Equal(KodoState.Healthy, Step(ref memory, _green, _start.AddMinutes(5)).Underlying);
    }

    [Fact]
    public void AnyNonGreenSampleRestartsTheGreenRun()
    {
        // Section 8.2, hysteresis: a sample that is not all green resets the run, so the window starts again.
        KodoEvaluatorMemory memory = new(KodoState.Degraded, null, [], false);

        Step(ref memory, _green, _start);
        Step(ref memory, _amber, _start.AddMinutes(2));
        Step(ref memory, _green, _start.AddMinutes(3));

        Assert.Equal(KodoState.Degraded, Step(ref memory, _green, _start.AddMinutes(7)).Underlying);
        Assert.Equal(KodoState.Healthy, Step(ref memory, _green, _start.AddMinutes(8)).Underlying);
    }

    [Fact]
    public void FailingDoesNotFlipToHealthyOnGreenSamplesWithoutRecovery()
    {
        // Section 8.2: failing -> healthy is not used directly. A green sample, however long, is not a recovery.
        KodoEvaluatorMemory memory = new(KodoState.Failing, null, [], false);

        Assert.Equal(KodoState.Failing, Step(ref memory, _green, _start).Underlying);
        Assert.Equal(KodoState.Failing, Step(ref memory, _green, _start.AddMinutes(10)).Underlying);
    }

    [Fact]
    public void RecoveringNeedsTheWholeWindowBeforeHealthy()
    {
        // Section 8.2: recovering -> healthy needs the stability window with all rules green.
        KodoEvaluatorMemory memory = new(KodoState.Recovering, null, [], false);

        Assert.Equal(KodoState.Recovering, Step(ref memory, _green, _start).Underlying);
        Assert.Equal(KodoState.Recovering, Step(ref memory, _green, _start.AddMinutes(4)).Underlying);
        Assert.Equal(KodoState.Healthy, Step(ref memory, _green, _start.AddMinutes(5)).Underlying);
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
            _criticalRed,
            _recoveryRunning,
            _recoveryFailed,
            _recoveryRunning,
            _recoveryFailed,
            _recoveryRunning,
            _recoveryFailed,
            _recoveryRunning,
        ];

        for (int i = 0; i < steps.Length; i++)
        {
            KodoEvaluation result = Step(ref memory, steps[i], _start.AddMinutes(i));
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
            (0, _amber), (1, _green), (6, _green), (7, _amber), (8, _green), (13, _green), (14, _amber),
            (15, _green), (20, _green), (21, _amber), // seventh transition: flapping starts here
        };
        foreach ((int minute, KodoObservation observation) in schedule)
        {
            Step(ref memory, observation, _start.AddMinutes(minute));
        }

        Assert.True(memory.Flapping);

        // Healthy would be eligible here (green since minute 22, for 5 minutes), but flapping holds it degraded.
        KodoEvaluation held = Step(ref memory, _green, _start.AddMinutes(22));
        KodoEvaluation heldLater = Step(ref memory, _green, _start.AddMinutes(27));
        Assert.Equal(KodoState.Degraded, held.Underlying);
        Assert.Equal(KodoState.Degraded, heldLater.Underlying);
        Assert.True(heldLater.Flapping);
        Assert.False(heldLater.RaiseFlapAlert);

        // Once every transition has left the 30 minute window, the normal rules apply again.
        KodoEvaluation released = Step(ref memory, _green, _start.AddMinutes(52));
        Assert.Equal(KodoState.Healthy, released.Underlying);
        Assert.False(released.Flapping);
    }

    [Fact]
    public void FlapDoesNotHideCriticalRed()
    {
        // Section 8.2 forces degraded only over healthy or degraded. A critical red stays failing.
        KodoEvaluatorMemory memory = new(KodoState.Healthy, null, [.. Enumerable.Range(0, 6).Select(i => _start.AddMinutes(i))], true);

        KodoEvaluation result = Step(ref memory, _criticalRed, _start.AddMinutes(7));

        Assert.True(result.Flapping);
        Assert.Equal(KodoState.Failing, result.Underlying);
    }

    [Fact]
    public void IncidentModeShowsFrozenBesideTheUnderlyingState()
    {
        // Section 8.1 and 16.3: frozen is shown beside the underlying health, which is still computed.
        KodoEvaluatorMemory memory = new(KodoState.Failing, null, [], false);
        KodoObservation incident = _criticalRed with { IncidentActive = true };

        KodoEvaluation result = Step(ref memory, incident, _start);

        Assert.Equal(KodoState.Frozen, result.State);
        Assert.Equal(KodoState.Failing, result.Underlying);
    }

    [Fact]
    public void ExitingIncidentModeShowsTheReEvaluatedState()
    {
        // Section 8.2: frozen -> previous or re-evaluated state once incident mode exits.
        KodoEvaluatorMemory memory = new(KodoState.Failing, null, [], false);
        Step(ref memory, _criticalRed with { IncidentActive = true }, _start);

        KodoEvaluation result = Step(ref memory, _criticalRed, _start.AddMinutes(1));

        Assert.Equal(KodoState.Failing, result.State);
        Assert.Equal(KodoState.Failing, result.Underlying);
    }

    [Fact]
    public void EvaluationIsDeterministicForTheSameTime()
    {
        // The evaluator reads no clock, so the same inputs always give the same result.
        KodoEvaluatorMemory memory = new(KodoState.Degraded, _start, [], false);

        KodoEvaluation first = KodoEvaluator.Evaluate(memory, _green, _start.AddMinutes(5), _settings);
        KodoEvaluation second = KodoEvaluator.Evaluate(memory, _green, _start.AddMinutes(5), _settings);

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

        Assert.Throws<ArgumentOutOfRangeException>(() => KodoEvaluator.Evaluate(KodoEvaluator.Initial, _green, _start, settings));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FlapLimitBelowOneIsRejected(int limit)
    {
        // Section 20: kodo.state.flap_limit must allow at least one transition.
        var settings = new KodoEvaluatorSettings { FlapLimit = limit };

        Assert.Throws<ArgumentOutOfRangeException>(() => KodoEvaluator.Evaluate(KodoEvaluator.Initial, _green, _start, settings));
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
