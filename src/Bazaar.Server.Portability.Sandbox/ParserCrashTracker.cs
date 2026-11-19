namespace Bazaar.Server.Portability.Sandbox;

/// <summary>
/// Counts abnormal parser child endings per importer. When <see cref="ParserSandboxOptions.CrashLoopThreshold"/> of them
/// fall inside <see cref="ParserSandboxOptions.CrashLoopWindow"/>, it raises <c>parser_crash_loop</c> once and refuses
/// further jobs for that importer until the window has passed with no new failure (crash-only design, doc 46 hooks).
/// A clean job resets the count.
/// </summary>
public sealed class ParserCrashTracker
{
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly IParserSignalSink? _sink;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    public ParserCrashTracker(int threshold, TimeSpan window, IParserSignalSink? sink = null, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _threshold = threshold;
        _window = window;
        _sink = sink;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>True while the circuit for this importer is open.</summary>
    public bool IsOpen(string importerId)
    {
        lock (_gate)
        {
            return _states.TryGetValue(importerId, out var state) && state.OpenUntil is { } until && until > _time.GetUtcNow();
        }
    }

    /// <summary>Throws <see cref="ParserFailureKind.CrashLoop"/> while the circuit is open.</summary>
    public void EnsureClosed(string importerId)
    {
        if (IsOpen(importerId))
        {
            throw new ParserSandboxException(
                ParserFailureKind.CrashLoop,
                $"parser for '{importerId}' is in a crash loop ({ParserSignals.CrashLoop}); jobs are refused until it has been quiet for {_window}");
        }
    }

    /// <summary>Records an abnormal ending. Raises the signal when this one opens the circuit.</summary>
    public void RecordFailure(string importerId, string detail)
    {
        ParserSignal? signal = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
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

            while (state.Failures.Count > 0 && now - state.Failures.Peek() > _window)
            {
                state.Failures.Dequeue();
            }

            state.Failures.Enqueue(now);
            if (state.Failures.Count >= _threshold)
            {
                var wasOpen = state.OpenUntil is not null;
                state.OpenUntil = now + _window;
                if (!wasOpen)
                {
                    signal = new ParserSignal(
                        ParserSignals.CrashLoop,
                        importerId,
                        $"{state.Failures.Count} abnormal endings within {_window}; last: {detail}");
                }
            }
        }

        if (signal is not null) _sink?.Raise(signal);
    }

    /// <summary>A clean job resets the count for that importer.</summary>
    public void RecordSuccess(string importerId)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(importerId, out var state) && state.OpenUntil is null)
            {
                state.Failures.Clear();
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

    private sealed class State
    {
        public Queue<DateTimeOffset> Failures { get; } = new();
        public DateTimeOffset? OpenUntil { get; set; }
    }
}
