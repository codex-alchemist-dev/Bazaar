namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// Agent-keyed counters: wake timestamps in a sliding one-hour window, and the run of identical tool calls.
/// Not thread-safe; the facade holds its lock around every call.
/// </summary>
internal sealed class AgentCounters
{
    /// <summary>A wake counts for one hour. It expires when the clock reaches its time plus this window.</summary>
    public static readonly TimeSpan WakeWindow = TimeSpan.FromHours(1);

    private readonly List<DateTimeOffset> _wakes = [];
    private readonly List<DateTimeOffset> _scoredWakes = [];
    private string? _lastFingerprint;
    private int _repeatCount;

    /// <summary>Wakes in the window ending at <paramref name="now"/>, all kinds.</summary>
    public int WakesInWindow(DateTimeOffset now)
    {
        Prune(_wakes, now);
        return _wakes.Count;
    }

    /// <summary>Scored wakes in the window ending at <paramref name="now"/>.</summary>
    public int ScoredWakesInWindow(DateTimeOffset now)
    {
        Prune(_scoredWakes, now);
        return _scoredWakes.Count;
    }

    /// <summary>Records one wake at <paramref name="now"/>.</summary>
    public void RecordWake(DateTimeOffset now, WakeKind kind)
    {
        Prune(_wakes, now);
        _wakes.Add(now);

        if (kind == WakeKind.SkimmerScored)
        {
            Prune(_scoredWakes, now);
            _scoredWakes.Add(now);
        }
    }

    /// <summary>
    /// Records one tool call and returns how many identical calls in a row this now is (1 for a new call). A different
    /// fingerprint restarts the run.
    /// </summary>
    public int RecordToolCall(string fingerprint)
    {
        if (fingerprint == _lastFingerprint)
        {
            _repeatCount++;
        }
        else
        {
            _lastFingerprint = fingerprint;
            _repeatCount = 1;
        }

        return _repeatCount;
    }

    private static void Prune(List<DateTimeOffset> wakes, DateTimeOffset now) =>
        wakes.RemoveAll(wake => now - wake >= WakeWindow);
}
