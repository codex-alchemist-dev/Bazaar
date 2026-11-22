namespace Bazaar.Server.Gateway.Selection;

/// <summary>Tuning for <see cref="EndpointStatsTracker"/>. Defaults are the shipped safe values.</summary>
public sealed record StatsOptions
{
    /// <summary>Counters lose half their weight after this long, so an idle endpoint's old failures fade.</summary>
    public TimeSpan CounterHalfLife { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Time constant of the peak-EWMA latency: after a spike it decays toward new samples over about this long.</summary>
    public TimeSpan LatencyTau { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Recent latencies kept per endpoint for the p95 estimate.</summary>
    public int LatencyWindow { get; init; } = 64;

    /// <summary>How long an affinity key is remembered. Provider prompt caches live minutes to an hour.</summary>
    public TimeSpan AffinityTtl { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Most affinity keys remembered; the oldest go first.</summary>
    public int MaxAffinityKeys { get; init; } = 4096;
}

/// <summary>
/// In-memory feedback store: time-decayed success and failure counters, a peak-EWMA latency (a spike counts at once and
/// fades, so a slow endpoint is not forgotten by an average), a p95 estimate from a small window, an in-flight count, and
/// the affinity map. Thread safe. Holds no secret and no prompt.
/// </summary>
public sealed class EndpointStatsTracker : IEndpointStatsTracker
{
    private readonly StatsOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<EndpointKey, State> _states = [];
    private readonly Dictionary<string, (EndpointKey Key, DateTimeOffset At)> _affinity = [];

    public EndpointStatsTracker(StatsOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new StatsOptions();
        _time = time ?? TimeProvider.System;
        if (_options.LatencyWindow < 4 || _options.MaxAffinityKeys < 1 || _options.CounterHalfLife <= TimeSpan.Zero || _options.LatencyTau <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Stats options must be positive (window at least 4).");
        }
    }

    public IAttempt Begin(EndpointKey key, string? affinityKey = null)
    {
        lock (_gate)
        {
            Get(key).InFlight++;
        }

        return new Attempt(this, key, affinityKey);
    }

    public EndpointStats Snapshot(EndpointKey key)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var s))
            {
                return new EndpointStats(0, 0, null, null, 0);
            }

            Decay(s, _time.GetUtcNow());
            return new EndpointStats(
                s.Successes,
                s.Failures,
                s.HasLatency ? TimeSpan.FromMilliseconds(s.EwmaMs) : null,
                P95(s),
                s.InFlight);
        }
    }

    public EndpointKey? AffinityHolder(string affinityKey)
    {
        ArgumentNullException.ThrowIfNull(affinityKey);
        lock (_gate)
        {
            if (_affinity.TryGetValue(affinityKey, out var entry) && _time.GetUtcNow() - entry.At <= _options.AffinityTtl)
            {
                return entry.Key;
            }

            return null;
        }
    }

    private State Get(EndpointKey key)
    {
        if (!_states.TryGetValue(key, out var s))
        {
            s = new State(_options.LatencyWindow) { LastDecay = _time.GetUtcNow() };
            _states[key] = s;
        }

        return s;
    }

    private void Decay(State s, DateTimeOffset now)
    {
        var dt = now - s.LastDecay;
        if (dt <= TimeSpan.Zero)
        {
            return;
        }

        var factor = Math.Pow(0.5, dt / _options.CounterHalfLife);
        s.Successes *= factor;
        s.Failures *= factor;
        s.LastDecay = now;
    }

    private void Finish(EndpointKey key, string? affinityKey, AttemptOutcome outcome)
    {
        lock (_gate)
        {
            var s = Get(key);
            s.InFlight = Math.Max(0, s.InFlight - 1);
            var now = _time.GetUtcNow();
            Decay(s, now);
            switch (outcome.Kind)
            {
                case AttemptOutcomeKind.Success:
                    s.Successes += 1;
                    if (affinityKey is not null)
                    {
                        RememberAffinity(affinityKey, key, now);
                    }

                    break;
                case AttemptOutcomeKind.Failure:
                    s.Failures += 1;
                    break;
            }

            if (outcome.Kind is AttemptOutcomeKind.Success && (outcome.TimeToFirstEvent ?? outcome.Total) is { } latency)
            {
                Observe(s, latency.TotalMilliseconds, now);
            }
            else if (outcome.Kind is AttemptOutcomeKind.Failure && outcome.Total is { } failedAfter)
            {
                // A slow failure is a slow endpoint: it feeds latency too.
                Observe(s, failedAfter.TotalMilliseconds, now);
            }
        }
    }

    private void Observe(State s, double ms, DateTimeOffset now)
    {
        if (!s.HasLatency)
        {
            s.EwmaMs = ms;
            s.HasLatency = true;
            s.LastLatencyAt = now;
        }
        else if (ms > s.EwmaMs)
        {
            s.EwmaMs = ms;
            s.LastLatencyAt = now;
        }
        else
        {
            var w = Math.Exp(-(now - s.LastLatencyAt).TotalMilliseconds / _options.LatencyTau.TotalMilliseconds);
            s.EwmaMs = (s.EwmaMs * w) + (ms * (1 - w));
            s.LastLatencyAt = now;
        }

        s.Window[s.WindowNext] = ms;
        s.WindowNext = (s.WindowNext + 1) % s.Window.Length;
        s.WindowCount = Math.Min(s.WindowCount + 1, s.Window.Length);
    }

    private static TimeSpan? P95(State s)
    {
        if (s.WindowCount < 5)
        {
            return null;
        }

        var copy = new double[s.WindowCount];
        Array.Copy(s.Window, copy, s.WindowCount);
        Array.Sort(copy);
        var index = (int)Math.Ceiling(0.95 * copy.Length) - 1;
        return TimeSpan.FromMilliseconds(copy[Math.Clamp(index, 0, copy.Length - 1)]);
    }

    private void RememberAffinity(string affinityKey, EndpointKey key, DateTimeOffset now)
    {
        _affinity[affinityKey] = (key, now);
        if (_affinity.Count <= _options.MaxAffinityKeys)
        {
            return;
        }

        foreach (var expired in _affinity.Where(p => now - p.Value.At > _options.AffinityTtl).Select(p => p.Key).ToList())
        {
            _affinity.Remove(expired);
        }

        if (_affinity.Count > _options.MaxAffinityKeys)
        {
            foreach (var oldest in _affinity.OrderBy(p => p.Value.At).Take(_affinity.Count - _options.MaxAffinityKeys).Select(p => p.Key).ToList())
            {
                _affinity.Remove(oldest);
            }
        }
    }

    private sealed class State(int window)
    {
        public double Successes;
        public double Failures;
        public double EwmaMs;
        public bool HasLatency;
        public int InFlight;
        public DateTimeOffset LastDecay;
        public DateTimeOffset LastLatencyAt;
        public double[] Window { get; } = new double[window];
        public int WindowNext;
        public int WindowCount;
    }

    private sealed class Attempt(EndpointStatsTracker owner, EndpointKey key, string? affinityKey) : IAttempt
    {
        private int _done;

        public void Complete(AttemptOutcome outcome)
        {
            ArgumentNullException.ThrowIfNull(outcome);
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.Finish(key, affinityKey, outcome);
            }
        }

        public void Dispose() => Complete(new AttemptOutcome(AttemptOutcomeKind.Abandoned));
    }
}
