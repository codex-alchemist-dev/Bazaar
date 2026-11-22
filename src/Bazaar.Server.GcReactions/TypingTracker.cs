using System.Collections.Concurrent;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// The short in-memory record behind the typing signal (docs/design/02, "Typing signal"). It exists only to rate-limit and to
/// prune: it holds when each person last sent a signal in each GC, never anything that is persisted or replayed. A restart
/// loses it, which costs a few seconds of indicator.
/// </summary>
public sealed class TypingTracker(TimeProvider time)
{
    private readonly ConcurrentDictionary<(Guid Gc, Guid Who), DateTimeOffset> _last = new();
    private int _calls;

    /// <summary>
    /// True when the signal should be taken. A stop is always taken and clears the record. A start or refresh inside
    /// <paramref name="minInterval"/> of the last accepted one is dropped.
    /// </summary>
    public bool TryAccept(Guid gcId, Guid principalId, bool typing, TimeSpan minInterval, TimeSpan ttl)
    {
        var now = time.GetUtcNow();
        var key = (gcId, principalId);
        if (!typing)
        {
            _last.TryRemove(key, out _);
            return true;
        }

        var accepted = false;
        _last.AddOrUpdate(
            key,
            _ =>
            {
                accepted = true;
                return now;
            },
            (_, previous) =>
            {
                if (now - previous < minInterval)
                    return previous;
                accepted = true;
                return now;
            });

        if (Interlocked.Increment(ref _calls) % 256 == 0)
        {
            foreach (var (k, at) in _last)
            {
                if (now - at >= ttl)
                    _last.TryRemove(k, out _);
            }
        }

        return accepted;
    }

    /// <summary>People with a live record, for tests and diagnostics.</summary>
    public int Count => _last.Count;
}
