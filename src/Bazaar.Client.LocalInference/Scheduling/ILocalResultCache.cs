using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Client.LocalInference.Scheduling;

/// <summary>Result store keyed by the scheduler's composed key (cache key plus model version, doc 31, Design: Scheduling).</summary>
public interface ILocalResultCache
{
    bool TryGet(string key, [NotNullWhen(true)] out LocalTaskResult? result);

    void Store(string key, LocalTaskResult result);
}

/// <summary>Bounded in-memory cache. When full, the oldest inserted entry is evicted first.</summary>
public sealed class InMemoryLocalResultCache : ILocalResultCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, LocalTaskResult> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _lock = new();

    public InMemoryLocalResultCache(int maxEntries = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _maxEntries = maxEntries;
    }

    public bool TryGet(string key, [NotNullWhen(true)] out LocalTaskResult? result)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(key, out result);
        }
    }

    public void Store(string key, LocalTaskResult result)
    {
        lock (_lock)
        {
            if (_entries.ContainsKey(key))
            {
                _entries[key] = result;
                return;
            }

            if (_entries.Count >= _maxEntries)
            {
                _entries.Remove(_insertionOrder.Dequeue());
            }

            _entries.Add(key, result);
            _insertionOrder.Enqueue(key);
        }
    }
}
