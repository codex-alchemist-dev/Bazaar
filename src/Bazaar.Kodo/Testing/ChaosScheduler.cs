namespace Bazaar.Kodo.Testing;

/// <summary>
/// Chooses when to run a chaos experiment and which (doc 46 section 17.3). Pure logic: a seeded random source, an injected clock,
/// an allow-list of kinds, and a sandbox behind an interface. It never starts anything unless the options are enabled, the gate
/// is open (authorised, no incident), the sandbox says it is a sandbox, and the kind is on the allow-list. While an experiment
/// runs it watches the gate and the duration limit and aborts within the abort deadline.
/// </summary>
public sealed class ChaosScheduler
{
    private readonly ChaosOptions _options;
    private readonly IChaosGate _gate;
    private readonly IChaosSandbox _sandbox;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly ChaosFaultKind[] _kinds;
    private readonly Lock _lock = new();
    private DateTimeOffset? _nextDue;
    private int _sequence;
    private bool _running;

    public ChaosScheduler(ChaosOptions options, IChaosGate gate, IChaosSandbox sandbox, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(sandbox);
        options.Validate();

        _options = options;
        _gate = gate;
        _sandbox = sandbox;
        _time = timeProvider ?? TimeProvider.System;
        _random = new Random(options.Seed);
        _kinds = options.AllowedKinds.OrderBy(kind => kind).ToArray();
    }

    /// <summary>
    /// The decision for now. A <see cref="ChaosDecisionKind.Start"/> decision consumes the random draws, schedules the next
    /// experiment and marks the scheduler busy until <see cref="RunDueAsync"/> finishes it, so call it only to act on it.
    /// </summary>
    public ChaosDecision Next()
    {
        lock (_lock)
        {
            if (!_options.Enabled)
            {
                return new ChaosDecision(ChaosDecisionKind.Disabled, null, null);
            }

            ChaosGateState gate = _gate.Current;
            if (gate.IncidentOpen)
            {
                return new ChaosDecision(ChaosDecisionKind.IncidentOpen, null, _nextDue);
            }

            if (!gate.Authorized)
            {
                return new ChaosDecision(ChaosDecisionKind.GateClosed, null, _nextDue);
            }

            if (!_sandbox.IsSandbox)
            {
                return new ChaosDecision(ChaosDecisionKind.NotSandbox, null, _nextDue);
            }

            if (_kinds.Length == 0)
            {
                return new ChaosDecision(ChaosDecisionKind.NoAllowedKinds, null, _nextDue);
            }

            if (_running)
            {
                return new ChaosDecision(ChaosDecisionKind.Busy, null, _nextDue);
            }

            DateTimeOffset now = _time.GetUtcNow();
            _nextDue ??= now + RandomInterval();
            if (now < _nextDue)
            {
                return new ChaosDecision(ChaosDecisionKind.NotDue, null, _nextDue);
            }

            ChaosFaultKind kind = _kinds[_random.Next(_kinds.Length)];
            int intensity = _random.Next(1, _options.MaxIntensityPercent + 1);
            ChaosExperiment experiment = new(++_sequence, kind, intensity, _options.MaxExperimentDuration, now);
            _nextDue = now + RandomInterval();
            _running = true;
            return new ChaosDecision(ChaosDecisionKind.Start, experiment, _nextDue);
        }
    }

    /// <summary>
    /// Runs the next experiment if one is due and returns its result, or null when none ran. The result is a sandbox report only
    /// (section 17.3).
    /// </summary>
    public async Task<ChaosExperimentResult?> RunDueAsync(CancellationToken cancellationToken)
    {
        ChaosDecision decision = Next();
        if (decision.Kind != ChaosDecisionKind.Start || decision.Experiment is not { } experiment)
        {
            return null;
        }

        try
        {
            return await ExecuteAsync(experiment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                _running = false;
            }
        }
    }

    private async Task<ChaosExperimentResult> ExecuteAsync(ChaosExperiment experiment, CancellationToken cancellationToken)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ChaosExperimentResult> run = Task.Run(() => _sandbox.RunAsync(experiment, stop.Token), CancellationToken.None);
        DateTimeOffset deadline = experiment.StartedAt + experiment.MaxDuration;

        while (!run.IsCompleted)
        {
            Task watch = Task.Delay(_options.WatchInterval, _time, CancellationToken.None);
            await Task.WhenAny(run, watch).ConfigureAwait(false);
            if (run.IsCompleted)
            {
                break;
            }

            bool abort = !_gate.Current.Open || _time.GetUtcNow() >= deadline || cancellationToken.IsCancellationRequested;
            if (!abort)
            {
                continue;
            }

            // The watch interval is part of the 10 second budget, so the sandbox gets what is left of the deadline.
            stop.Cancel();
            Task grace = Task.Delay(_options.AbortDeadline - _options.WatchInterval, _time, CancellationToken.None);
            Task first = await Task.WhenAny(run, grace).ConfigureAwait(false);
            return first == run
                ? await Settle(experiment, run, ChaosOutcome.Aborted).ConfigureAwait(false)
                : Failure(experiment, ChaosOutcome.AbortTimedOut);
        }

        return await Settle(experiment, run, ChaosOutcome.Completed).ConfigureAwait(false);
    }

    private static async Task<ChaosExperimentResult> Settle(ChaosExperiment experiment, Task<ChaosExperimentResult> run, ChaosOutcome outcome)
    {
        try
        {
            ChaosExperimentResult result = await run.ConfigureAwait(false);
            return outcome == ChaosOutcome.Aborted ? result with { Outcome = ChaosOutcome.Aborted, HypothesisHeld = false } : result;
        }
        catch (OperationCanceledException) when (outcome == ChaosOutcome.Aborted)
        {
            return Failure(experiment, ChaosOutcome.Aborted);
        }
        catch (Exception)
        {
            return Failure(experiment, ChaosOutcome.Failed);
        }
    }

    // The scheduler cannot vouch for anything the sandbox did not report, so a failure result claims no pass: the hypothesis did
    // not hold, and the data-loss invariant is treated as held only because the scheduler has no evidence against it.
    private static ChaosExperimentResult Failure(ChaosExperiment experiment, ChaosOutcome outcome) =>
        new(experiment, outcome, HypothesisHeld: false, Recovery: null, DataLossInvariantHeld: true,
            ExpectedAlertFired: false, UnexpectedCriticalAlert: false, RunbookSucceeded: null);

    private TimeSpan RandomInterval()
    {
        double span = (_options.MaxInterval - _options.MinInterval).TotalSeconds;
        return _options.MinInterval + TimeSpan.FromSeconds(span * _random.NextDouble());
    }
}
