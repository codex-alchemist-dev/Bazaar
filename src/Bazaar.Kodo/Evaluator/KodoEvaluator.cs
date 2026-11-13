namespace Bazaar.Kodo.Evaluator;

/// <summary>
/// The pure state transition for Kodo (doc 46 section 8). It does no I/O and reads no clock: the caller
/// passes the sample time, so the same inputs always give the same state and the tests need no waiting.
/// </summary>
public static class KodoEvaluator
{
    /// <summary>The memory of a fresh install: healthy, with no history.</summary>
    public static KodoEvaluatorMemory Initial { get; } = new(KodoState.Healthy, null, [], false);

    /// <summary>
    /// Evaluates one sample.
    /// </summary>
    /// <param name="previous">The memory returned by the last evaluation (or <see cref="Initial"/>).</param>
    /// <param name="observation">The probe and recovery facts at <paramref name="now"/>.</param>
    /// <param name="now">The sample time. Always passed in, never read from the clock.</param>
    /// <param name="settings">The stability and flap settings (section 20).</param>
    public static KodoEvaluation Evaluate(
        KodoEvaluatorMemory previous,
        KodoObservation observation,
        DateTimeOffset now,
        KodoEvaluatorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        // Hysteresis (section 8.2): the green run starts at the first all-green sample and any sample that
        // is not all green resets it. One green sample therefore never clears a state.
        DateTimeOffset? greenSince = observation.AllRulesGreen ? previous.GreenSince ?? now : null;
        bool greenLongEnough = greenSince is { } since && now - since >= settings.StabilityWindow;

        KodoState candidate = Decide(previous.Underlying, observation, greenLongEnough);

        // Flap detection (section 8.2): count the changes in the window, this one included. More than the
        // limit means flapping. While flapping, healthy and degraded are held at degraded. Failing and
        // recovering are never overridden, because a critical red must not be hidden by a flap.
        List<DateTimeOffset> recent = previous.Transitions.Where(at => now - at < settings.FlapWindow).ToList();
        bool changed = candidate != previous.Underlying;
        bool flapping = recent.Count + (changed ? 1 : 0) > settings.FlapLimit;

        KodoState underlying = flapping && candidate is KodoState.Healthy or KodoState.Degraded
            ? KodoState.Degraded
            : candidate;

        List<DateTimeOffset> transitions = recent;
        if (underlying != previous.Underlying)
        {
            transitions.Add(now);
        }

        KodoEvaluatorMemory next = new(underlying, greenSince, transitions, flapping);
        KodoState shown = observation.IncidentActive ? KodoState.Frozen : underlying;
        return new KodoEvaluation(
            State: shown,
            Underlying: underlying,
            Flapping: flapping,
            RaiseFlapAlert: flapping && !previous.Flapping,
            Next: next);
    }

    private static KodoState Decide(KodoState previous, KodoObservation o, bool greenLongEnough)
    {
        bool failure = o.CriticalRed || o.IntegrityOrContractFailure || o.CrashLoop;
        bool degrading = o.CriticalUnknown
            || o.NonCriticalRedBeyondGrace > 0
            || o.NonCriticalAmberBeyondGrace
            || o.SloWarningBurn;
        bool healthyEligible = o.AllRulesGreen && greenLongEnough && !failure && !degrading;

        return previous switch
        {
            // Section 8.2, healthy -> failing (critical red, integrity or contract failure, crash loop)
            // and healthy -> degraded (non-critical red or amber past grace, SLO warning burn, critical unknown).
            KodoState.Healthy when failure => KodoState.Failing,
            KodoState.Healthy when degrading => KodoState.Degraded,
            KodoState.Healthy => KodoState.Healthy,

            // Section 8.2, degraded -> failing (any critical red, or a second non-critical red that breaches
            // an SLO) and degraded -> healthy (all rules green for the stability window).
            KodoState.Degraded when failure || (o.NonCriticalRedBeyondGrace >= 2 && o.SloBreach) => KodoState.Failing,
            KodoState.Degraded when healthyEligible => KodoState.Healthy,
            KodoState.Degraded => KodoState.Degraded,

            // Section 8.2, failing -> recovering when a recovery action starts. Failing -> healthy is not a
            // row of the table, so a green sample with no action leaves the state failing.
            KodoState.Failing when o.Recovery == RecoveryStatus.Running => KodoState.Recovering,
            KodoState.Failing => KodoState.Failing,

            // Section 8.2, recovering. A running action continues and nothing new starts. A failed or timed
            // out action goes to failing. Once the action is idle, rules still red after the verify step go
            // to failing, and a stability window with all rules green goes to healthy.
            KodoState.Recovering when o.Recovery == RecoveryStatus.Running => KodoState.Recovering,
            KodoState.Recovering when o.Recovery == RecoveryStatus.Failed => KodoState.Failing,
            KodoState.Recovering when failure || degrading => KodoState.Failing,
            KodoState.Recovering when healthyEligible => KodoState.Healthy,
            KodoState.Recovering => KodoState.Recovering,

            _ => throw new ArgumentOutOfRangeException(nameof(previous), previous, "The underlying state must be healthy, degraded, failing or recovering."),
        };
    }
}
