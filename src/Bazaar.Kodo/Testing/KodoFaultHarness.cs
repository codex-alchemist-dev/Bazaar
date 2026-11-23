using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Sentinel;

namespace Bazaar.Kodo.Testing;

/// <summary>One evaluated sample of a fault run.</summary>
/// <param name="Nominal">Time on the scenario timeline.</param>
/// <param name="Wall">The (possibly skewed) clock reading the evaluator was given.</param>
/// <param name="Observation">What the faults made the probes report.</param>
/// <param name="Evaluation">The evaluator's answer.</param>
/// <param name="Event">The stream event written for a change of the displayed state, or null.</param>
public sealed record FaultTick(
    TimeSpan Nominal,
    DateTimeOffset Wall,
    KodoObservation Observation,
    KodoEvaluation Evaluation,
    KodoStreamEventDto? Event);

/// <summary>The record of one run: every sample, the stream events and the restart decisions.</summary>
public sealed record FaultRun(
    IReadOnlyList<FaultTick> Ticks,
    IReadOnlyList<KodoStreamEventDto> Events,
    IReadOnlyList<RestartDecision> RestartDecisions)
{
    /// <summary>The displayed states in order, with repeats collapsed. It starts at healthy, the state before the first sample.</summary>
    public IReadOnlyList<KodoState> StateSequence
    {
        get
        {
            List<KodoState> states = [KodoState.Healthy];
            foreach (FaultTick tick in Ticks)
            {
                if (states[^1] != tick.Evaluation.State)
                {
                    states.Add(tick.Evaluation.State);
                }
            }

            return states;
        }
    }

    /// <summary>The number of samples that asked for the grouped flap alert (section 8.2).</summary>
    public int FlapAlertCount => Ticks.Count(tick => tick.Evaluation.RaiseFlapAlert);

    /// <summary>True when the stream revisions are 1, 2, 3 and so on, with no gap and no repeat (no event was lost).</summary>
    public bool EventsGapFree => Events.Select((e, i) => e.Revision == i + 1).All(ok => ok);
}

/// <summary>
/// What a fault run must show (doc 46 section 17.1: the state transition, the alert and no data loss). Audit entries are the
/// state-changed stream events here, because the journal is not part of the pure core.
/// </summary>
public sealed record FaultExpectation
{
    /// <summary>The displayed states in order, repeats collapsed. Null means do not check.</summary>
    public IReadOnlyList<KodoState>? StateSequence { get; init; }

    /// <summary>How many flap alerts must be raised. Null means do not check.</summary>
    public int? FlapAlerts { get; init; }

    /// <summary>Whether the stream must have exactly one state-changed event per displayed change and no revision gap.</summary>
    public bool RequireCompleteAuditTrail { get; init; } = true;

    /// <summary>Returns the failed expectations as sentences. An empty list means the run met them.</summary>
    public IReadOnlyList<string> Check(FaultRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        List<string> failures = [];
        if (StateSequence is { } expected && !expected.SequenceEqual(run.StateSequence))
        {
            failures.Add($"State sequence was [{string.Join(", ", run.StateSequence)}], expected [{string.Join(", ", expected)}].");
        }

        if (FlapAlerts is { } alerts && alerts != run.FlapAlertCount)
        {
            failures.Add($"Flap alerts were {run.FlapAlertCount}, expected {alerts}.");
        }

        if (RequireCompleteAuditTrail)
        {
            int changes = run.StateSequence.Count - 1;
            int stateEvents = run.Events.Count(e => e.Type == KodoStreamEventDto.StateChangedType);
            if (stateEvents != changes)
            {
                failures.Add($"The stream has {stateEvents} state-changed events for {changes} state changes.");
            }

            if (!run.EventsGapFree)
            {
                failures.Add("The stream revisions have a gap or a repeat.");
            }
        }

        return failures;
    }
}

/// <summary>
/// Drives the real evaluator, stream writer and restart backoff with scripted faults, on fakes only and without waiting
/// (doc 46 section 17.1). It is deterministic: one script and one set of settings always give the same run. It has no I/O and
/// reads no clock; the "clock" is a counter it advances itself.
/// </summary>
/// <remarks>
/// Faults are mapped onto the observation shape of section 8 as follows (PROPOSED, see 17.5). A critical probe failure is
/// <c>CriticalRed</c>. A non-critical one is one red probe past its grace period. Stale data is <c>CriticalUnknown</c>, never
/// green. A restart storm feeds <see cref="RestartBackoff"/> and raises <c>CrashLoop</c> while the starts inside the window reach
/// the limit (section 7.1, default 5 in 10 minutes). A clock jump moves the time handed to the evaluator.
/// </remarks>
public sealed class KodoFaultHarness
{
    private readonly KodoEvaluatorSettings _settings;
    private readonly TimeSpan _tick;
    private readonly DateTimeOffset _start;
    private readonly int _crashLoopStarts;
    private readonly TimeSpan _crashLoopWindow;

    public KodoFaultHarness(
        KodoEvaluatorSettings? settings = null,
        TimeSpan? tick = null,
        DateTimeOffset? start = null,
        int crashLoopStarts = 5,
        TimeSpan? crashLoopWindow = null)
    {
        _settings = settings ?? new KodoEvaluatorSettings();
        _settings.Validate();
        _tick = tick ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_tick, TimeSpan.Zero);
        _start = start ?? new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(crashLoopStarts, 2);
        _crashLoopStarts = crashLoopStarts;
        _crashLoopWindow = crashLoopWindow ?? TimeSpan.FromMinutes(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_crashLoopWindow, TimeSpan.Zero);
    }

    /// <summary>Runs the script for <paramref name="duration"/>, sampling at every tick including time zero.</summary>
    public FaultRun Run(FaultScript script, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);

        KodoStreamWriter stream = new();
        RestartBackoff backoff = new(RestartBackoff.DefaultScheduleSeconds, RestartBackoff.DefaultStablePeriod);
        KodoEvaluatorMemory memory = KodoEvaluator.Initial;
        KodoState displayed = KodoState.Healthy;

        List<FaultTick> ticks = [];
        List<KodoStreamEventDto> events = [];
        List<RestartDecision> decisions = [];
        List<DateTimeOffset> restartStarts = [];
        List<(TimeSpan Until, bool Critical)> probeFailures = [];
        TimeSpan staleUntil = TimeSpan.MinValue;
        TimeSpan skew = TimeSpan.Zero;
        int nextFault = 0;

        for (TimeSpan nominal = TimeSpan.Zero; nominal <= duration; nominal += _tick)
        {
            while (nextFault < script.Faults.Count && script.Faults[nextFault].At <= nominal)
            {
                ScriptedFault fault = script.Faults[nextFault++];
                switch (fault.Kind)
                {
                    case FaultKind.ClockJump:
                        skew += fault.ClockOffset;
                        break;
                    case FaultKind.ProbeFailure:
                        probeFailures.Add((nominal + fault.Duration, fault.Critical));
                        break;
                    case FaultKind.StaleData:
                        staleUntil = TimeSpan.FromTicks(Math.Max(staleUntil.Ticks, (nominal + fault.Duration).Ticks));
                        break;
                    case FaultKind.RestartStorm:
                        DateTimeOffset at = _start + nominal + skew;
                        for (int i = 0; i < fault.Restarts; i++)
                        {
                            decisions.Add(backoff.RecordExit(at - TimeSpan.FromSeconds(1), at));
                            restartStarts.Add(at);
                        }

                        break;
                    default:
                        throw new InvalidOperationException($"Unknown fault kind {fault.Kind}.");
                }
            }

            DateTimeOffset wall = _start + nominal + skew;
            bool criticalFailure = probeFailures.Any(f => f.Critical && nominal < f.Until);
            int nonCritical = probeFailures.Count(f => !f.Critical && nominal < f.Until);
            bool crashLoop = restartStarts.Count(s => wall - s < _crashLoopWindow) >= _crashLoopStarts;

            KodoObservation observation = new()
            {
                CriticalRed = criticalFailure,
                NonCriticalRedBeyondGrace = nonCritical,
                CriticalUnknown = nominal < staleUntil,
                CrashLoop = crashLoop,
            };

            KodoEvaluation evaluation = KodoEvaluator.Evaluate(memory, observation, wall, _settings);
            memory = evaluation.Next;

            KodoStreamEventDto? written = null;
            if (evaluation.State != displayed)
            {
                written = stream.StateChanged(displayed, evaluation, wall);
                events.Add(written);
                displayed = evaluation.State;
            }

            ticks.Add(new FaultTick(nominal, wall, observation, evaluation, written));
        }

        return new FaultRun(ticks, events, decisions);
    }
}
