namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>
/// Counts events per key over a sliding window and says when the count first reaches the threshold. Each key keeps only its last
/// <c>threshold</c> event times, so memory is bounded by the threshold times the number of tracked keys. Idle keys are dropped
/// first, and when the key limit is still reached the least recently seen key goes.
/// </summary>
/// <remarks>
/// After a fire, the key stays quiet for one window, so a burst gives one alert per window. Events are counted under one lock, so
/// parallel events never lose a count.
/// </remarks>
internal sealed class SlidingWindowCounter<TKey>(int threshold, TimeSpan window, int maxKeys) where TKey : notnull
{
    /// <summary>The default key limit for one rule.</summary>
    public const int DefaultMaxKeys = 4096;

    private readonly Dictionary<TKey, Track> _tracks = new();
    private readonly object _gate = new();

    /// <summary>Records one event for <paramref name="key"/> at <paramref name="at"/>. Returns true when the rule should fire.</summary>
    public bool Observe(TKey key, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(key, out var track))
            {
                if (_tracks.Count >= maxKeys)
                {
                    EvictLocked(at);
                }

                track = new Track(at);
                _tracks.Add(key, track);
            }

            if (at > track.LastSeen)
            {
                track.LastSeen = at;
            }

            track.Times.Enqueue(at);
            if (track.Times.Count > threshold)
            {
                track.Times.Dequeue();
            }

            // The last `threshold` events fall inside the window when the oldest of them is still within it.
            if (track.Times.Count < threshold || at - track.Times.Peek() >= window)
            {
                return false;
            }

            if (track.LastFired is { } fired && at - fired < window)
            {
                return false;
            }

            track.LastFired = at;
            return true;
        }
    }

    private void EvictLocked(DateTimeOffset at)
    {
        foreach (var key in _tracks.Where(pair => IsIdle(pair.Value, at)).Select(pair => pair.Key).ToList())
        {
            _tracks.Remove(key);
        }

        if (_tracks.Count < maxKeys)
        {
            return;
        }

        _tracks.Remove(_tracks.MinBy(pair => pair.Value.LastSeen).Key);
    }

    private bool IsIdle(Track track, DateTimeOffset at) =>
        at - track.LastSeen >= window && (track.LastFired is not { } fired || at - fired >= window);

    private sealed class Track(DateTimeOffset firstSeen)
    {
        public DateTimeOffset LastSeen { get; set; } = firstSeen;

        public DateTimeOffset? LastFired { get; set; }

        public Queue<DateTimeOffset> Times { get; } = new();
    }
}
