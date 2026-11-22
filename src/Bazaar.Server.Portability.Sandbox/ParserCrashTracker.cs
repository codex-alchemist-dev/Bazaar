namespace Bazaar.Server.Portability.Sandbox;

/// <summary>
/// Counts parser child failures per importer and per source. The crash-loop circuit for an importer opens only when
/// <see cref="ParserSandboxOptions.CrashLoopThreshold"/> abnormal endings fall inside
/// <see cref="ParserSandboxOptions.CrashLoopWindow"/> and they come from at least <see cref="MinDistinctSources"/>
/// different sources, so one hostile file cannot shut an importer for every user. It raises <c>parser_crash_loop</c>
/// once and refuses further jobs for that importer until the window has passed with no new failure. Separately, one
/// source that has failed <see cref="ParserSandboxOptions.SourceFailureCap"/> times in the window is refused, so the
/// same file cannot be retried endlessly.
/// </summary>
public sealed class ParserCrashTracker
{
    /// <summary>An importer's circuit opens only when failures come from at least this many different sources.</summary>
    public const int MinDistinctSources = 2;

    private readonly int _threshold;
    private readonly int _sourceCap;
    private readonly TimeSpan _window;
    private readonly IParserSignalSink? _sink;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public ParserCrashTracker(
        int threshold,
        TimeSpan window,
        IParserSignalSink? sink = null,
        TimeProvider? time = null,
        int sourceFailureCap = ParserSandboxOptions.DefaultSourceFailureCap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceFailureCap, 1);
        _threshold = threshold;
        _window = window;
        _sink = sink;
        _time = time ?? TimeProvider.System;
        _sourceCap = sourceFailureCap;
    }

    /// <summary>How many importers the tracker currently holds state for. Aged-out importers are pruned.</summary>
    public int TrackedImporterCount
    {
        get
        {
            lock (_gate)
            {
                return _states.Count;
            }
        }
    }

    /// <summary>True while the circuit for this importer is open.</summary>
    public bool IsOpen(string importerId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(importerId, out var state) && state.OpenUntil is { } until && until > _time.GetUtcNow();
        }
    }

    /// <summary>True when this source has used up its failures for the window.</summary>
    public bool IsSourceBlocked(string importerId, string sourceKey)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(importerId, out var state)) return false;
            var now = _time.GetUtcNow();
            return state.Failures.Count(f => f.SourceKey == sourceKey && now - f.At <= _window) >= _sourceCap;
        }
    }

    /// <summary>Throws <see cref="ParserFailureKind.CrashLoop"/> while the circuit is open or the source is used up.</summary>
    public void EnsureClosed(string importerId, string sourceKey)
    {
        if (IsOpen(importerId))
        {
            throw new ParserSandboxException(
                ParserFailureKind.CrashLoop,
                $"parser for '{importerId}' is in a crash loop ({ParserSignals.CrashLoop}); jobs are refused until it has been quiet for {_window}");
        }

        if (IsSourceBlocked(importerId, sourceKey))
        {
            throw new ParserSandboxException(
                ParserFailureKind.CrashLoop,
                $"this source has failed {_sourceCap} times for '{importerId}' within {_window}; it is not retried again until the window passes");
        }
    }

    /// <summary>
    /// Records a failed job. It always counts against the source's cap; it counts toward the importer's circuit only when
    /// <paramref name="abnormal"/> (a crash, not an ordinary limit or parser error). Raises the signal when it opens the circuit.
    /// </summary>
    public void RecordFailure(string importerId, string sourceKey, string detail, bool abnormal = true)
    {
        ParserSignal? signal = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (!_states.TryGetValue(importerId, out var state))
            {
                state = new State();
                _states[importerId] = state;
            }

            if (state.OpenUntil is { } until && until <= now)
            {
                state.OpenUntil = null;
                state.Failures.Clear();
            }

            state.Failures.RemoveAll(f => now - f.At > _window);
            state.Failures.Add(new Failure(now, sourceKey, abnormal));

            var abnormalFailures = state.Failures.Where(f => f.Abnormal).ToList();
            if (abnormalFailures.Count >= _threshold && abnormalFailures.Select(f => f.SourceKey).Distinct().Count() >= MinDistinctSources)
            {
                var wasOpen = state.OpenUntil is not null;
                state.OpenUntil = now + _window;
                if (!wasOpen)
                {
                    signal = new ParserSignal(
                        ParserSignals.CrashLoop,
                        importerId,
                        $"{abnormalFailures.Count} abnormal endings from {abnormalFailures.Select(f => f.SourceKey).Distinct().Count()} sources within {_window}; last: {detail}");
                }
            }
        }

        if (signal is not null) _sink?.Raise(signal);
    }

    /// <summary>A clean job clears that source's failures for that importer.</summary>
    public void RecordSuccess(string importerId, string sourceKey)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(importerId, out var state))
            {
                state.Failures.RemoveAll(f => f.SourceKey == sourceKey);
            }
        }
    }

    /// <summary>Operator reset after the cause is fixed.</summary>
    public void Reset(string importerId)
    {
        lock (_gate)
        {
            _states.Remove(importerId);
        }
    }

    /// <summary>Drops importers whose circuit is closed and whose failures have all aged out, so the map cannot grow without bound.</summary>
    private void Prune(DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (var (id, state) in _states)
        {
            var open = state.OpenUntil is { } until && until > now;
            var recent = state.Failures.Any(f => now - f.At <= _window);
            if (!open && !recent) (stale ??= []).Add(id);
        }
        if (stale is not null)
        {
            foreach (var id in stale) _states.Remove(id);
        }
    }

    private sealed record Failure(DateTimeOffset At, string SourceKey, bool Abnormal);

    private sealed class State
    {
        public List<Failure> Failures { get; } = [];
        public DateTimeOffset? OpenUntil { get; set; }
    }
}
