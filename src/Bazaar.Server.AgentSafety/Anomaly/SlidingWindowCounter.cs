namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>
/// Counts events per key over a sliding window and says when the count first reaches the threshold. Each key keeps only its last
/// <c>threshold</c> event times, so memory is bounded by the threshold times the number of tracked keys.
/// </summary>
/// <remarks>
/// <para>
/// After a fire, the key stays quiet for one window, so a burst gives one alert per window. Events are counted under one lock, so
/// parallel events never lose a count. An event dated earlier than the key's latest is counted at that latest time, so a late event
/// never makes the window look emptier than it is. An event dated more than <see cref="MaxFutureSkew"/> ahead of the clock is counted
/// at the clock's now: a far-future time would otherwise make the key look idle for good and reset its window.
/// </para>
/// <para>
/// Whoever can create many keys can push another key out of a bounded table and reset its count, so eviction is per owner (the agent,
/// for the shipped rules). One owner holds at most <c>maxKeysPerOwner</c> keys and can only displace its own. When the whole table is
/// full, idle keys go first, then the weakest key overall. Within an owner, idle keys go first, then the key with the fewest events inside
/// the window (oldest first), so a key close to its threshold outlives a flood of fresh one-event keys.
/// </para>
/// </remarks>
internal sealed class SlidingWindowCounter<TKey>(
    int threshold,
    TimeSpan window,
    int maxKeys,
    Func<TKey, string> owner,
    TimeProvider? time = null,
    int maxKeysPerOwner = SlidingWindowCounter<TKey>.DefaultMaxKeysPerOwner)
    where TKey : notnull
{
    /// <summary>The default key limit for one rule.</summary>
    public const int DefaultMaxKeys = 4096;

    /// <summary>The default number of keys one owner may hold in one rule.</summary>
    public const int DefaultMaxKeysPerOwner = 64;

    /// <summary>How far ahead of the clock an event's time may be before it is counted at the clock's now.</summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(1);

    private readonly Dictionary<TKey, Track> _tracks = new();
    private readonly Dictionary<string, int> _ownerKeys = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();

    /// <summary>Records one event for <paramref name="key"/> at <paramref name="at"/>. Returns true when the rule should fire.</summary>
    public bool Observe(TKey key, DateTimeOffset at)
    {
        var now = _time.GetUtcNow();
        if (at > now + MaxFutureSkew)
        {
            at = now;
        }

        lock (_gate)
        {
            if (!_tracks.TryGetValue(key, out var track))
            {
                var keyOwner = owner(key);
                if (_ownerKeys.GetValueOrDefault(keyOwner) >= maxKeysPerOwner)
                {
                    EvictLocked(at, keyOwner);
                }

                if (_tracks.Count >= maxKeys)
                {
                    EvictLocked(at, null);
                }

                track = new Track(at, keyOwner);
                _tracks.Add(key, track);
                _ownerKeys[keyOwner] = _ownerKeys.GetValueOrDefault(keyOwner) + 1;
            }

            if (at < track.LastSeen)
            {
                at = track.LastSeen;
            }

            track.LastSeen = at;
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

    /// <summary>Frees one slot, among the keys of <paramref name="onlyOwner"/> when given, otherwise among all keys: idle keys first, then the weakest.</summary>
    private void EvictLocked(DateTimeOffset at, string? onlyOwner)
    {
        bool InScope(KeyValuePair<TKey, Track> pair) => onlyOwner is null || pair.Value.Owner == onlyOwner;

        foreach (var key in _tracks.Where(pair => InScope(pair) && IsIdle(pair.Value, at)).Select(pair => pair.Key).ToList())
        {
            RemoveLocked(key);
        }

        var full = onlyOwner is null ? _tracks.Count >= maxKeys : _ownerKeys.GetValueOrDefault(onlyOwner) >= maxKeysPerOwner;
        if (!full)
        {
            return;
        }

        var victim = _tracks.Where(InScope).MinBy(pair => (pair.Value.Times.Count, pair.Value.LastSeen));
        RemoveLocked(victim.Key);
    }

    private void RemoveLocked(TKey key)
    {
        if (!_tracks.Remove(key, out var track))
        {
            return;
        }

        if (_ownerKeys.TryGetValue(track.Owner, out var count) && count > 1)
        {
            _ownerKeys[track.Owner] = count - 1;
        }
        else
        {
            _ownerKeys.Remove(track.Owner);
        }
    }

    private bool IsIdle(Track track, DateTimeOffset at) =>
        at - track.LastSeen >= window && (track.LastFired is not { } fired || at - fired >= window);

    private sealed class Track(DateTimeOffset firstSeen, string owner)
    {
        public string Owner { get; } = owner;

        public DateTimeOffset LastSeen { get; set; } = firstSeen;

        public DateTimeOffset? LastFired { get; set; }

        public Queue<DateTimeOffset> Times { get; } = new();
    }
}
