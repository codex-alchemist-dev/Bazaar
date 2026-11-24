using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Testing;

/// <summary>One scheduled self-check (doc 46 section 17.2). Its result is an ordinary probe result.</summary>
public interface ISelfCheck
{
    /// <summary>The probe id, for example <c>selfcheck.checksums</c>.</summary>
    string Id { get; }

    Task<ProbeResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>A check with its cadence.</summary>
public sealed record ScheduledSelfCheck(ISelfCheck Check, TimeSpan Every);

/// <summary>One completed run of a self-check.</summary>
public sealed record SelfCheckRun(string Id, DateTimeOffset At, ProbeResult Result);

/// <summary>
/// Runs self-checks when they are due. Pure apart from the checks it is given: it reads time from the injected provider and
/// keeps its own last-run times. A check that throws is recorded as a red <c>probe_exception</c> run, so a broken check is never
/// silent. A check that has never run is due at once.
/// </summary>
/// <remarks>
/// PROPOSED (17.5): checks run only while the displayed state is healthy or degraded. Section 8.1 allows scheduled self-checks
/// in healthy and says nothing against degraded; during failing, recovering and frozen nothing new starts.
/// </remarks>
public sealed class SelfCheckRunner
{
    /// <summary>PROPOSED cadences from the table in section 9.1.</summary>
    public static readonly TimeSpan Daily = TimeSpan.FromDays(1);

    public static readonly TimeSpan Weekly = TimeSpan.FromDays(7);

    public static readonly TimeSpan Monthly = TimeSpan.FromDays(30);

    private readonly ScheduledSelfCheck[] _checks;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.Ordinal);

    public SelfCheckRunner(IEnumerable<ScheduledSelfCheck> checks, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        _checks = checks.ToArray();
        _time = timeProvider ?? TimeProvider.System;

        foreach (ScheduledSelfCheck scheduled in _checks)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scheduled.Every, TimeSpan.Zero);
        }

        if (_checks.Select(c => c.Check.Id).Distinct(StringComparer.Ordinal).Count() != _checks.Length)
        {
            throw new ArgumentException("Self-check ids must be unique.", nameof(checks));
        }
    }

    /// <summary>The ids of the checks that are due now, without running them.</summary>
    public IReadOnlyList<string> Due()
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _checks.Where(c => IsDue(c, now)).Select(c => c.Check.Id).ToList();
    }

    /// <summary>Runs every due check, in the order given, and returns the runs. Nothing runs unless <paramref name="state"/> allows it.</summary>
    public async Task<IReadOnlyList<SelfCheckRun>> RunDueAsync(KodoState state, CancellationToken cancellationToken)
    {
        if (state is not (KodoState.Healthy or KodoState.Degraded))
        {
            return [];
        }

        List<SelfCheckRun> runs = [];
        foreach (ScheduledSelfCheck scheduled in _checks)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (!IsDue(scheduled, now))
            {
                continue;
            }

            ProbeResult result;
            try
            {
                result = await scheduled.Check.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                result = ProbeResult.FromException(error);
            }

            _lastRun[scheduled.Check.Id] = now;
            runs.Add(new SelfCheckRun(scheduled.Check.Id, now, result));
        }

        return runs;
    }

    /// <summary>
    /// Folds self-check runs into an observation (PROPOSED, 17.5). The self-checks are non-critical (section 9.1), so a red run is
    /// one non-critical red past grace, and an amber or unknown run is non-critical amber. Green adds nothing.
    /// </summary>
    public static KodoObservation ToObservation(IEnumerable<SelfCheckRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        int red = 0;
        bool amber = false;
        foreach (SelfCheckRun run in runs)
        {
            switch (run.Result.Status)
            {
                case ProbeStatus.Red:
                    red++;
                    break;
                case ProbeStatus.Amber:
                case ProbeStatus.Unknown:
                    amber = true;
                    break;
            }
        }

        return new KodoObservation { NonCriticalRedBeyondGrace = red, NonCriticalAmberBeyondGrace = amber };
    }

    private bool IsDue(ScheduledSelfCheck scheduled, DateTimeOffset now) =>
        !_lastRun.TryGetValue(scheduled.Check.Id, out DateTimeOffset last) || now - last >= scheduled.Every;
}
