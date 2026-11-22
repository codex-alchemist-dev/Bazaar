using System.Security.Cryptography;

namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// A bounded least-recently-used cache of unwrapped DEKs. Evicted and disposed entries are zeroed. Callers receive their own
/// <see cref="ColumnDek"/> copy, so zeroing here never changes a key somebody is using.
/// </summary>
internal sealed class DekCache(int capacity) : IDisposable
{
    private sealed class Entry(Guid id, string scope, string kekId, bool retired, byte[] key)
    {
        public Guid Id { get; } = id;
        public string Scope { get; } = scope;
        public string KekId { get; } = kekId;
        public bool Retired { get; } = retired;
        public byte[] Key { get; } = key;
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = new();
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private bool _disposed;

    public int Count
    {
        get
        {
            lock (_gate)
                return _map.Count;
        }
    }

    /// <summary>A copy of the cached key, or null on a miss.</summary>
    public ColumnDek? TryGet(Guid dekId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_map.TryGetValue(dekId, out var node))
                return null;
            _order.Remove(node);
            _order.AddFirst(node);
            var e = node.Value;
            return new ColumnDek(e.Scope, e.Id, e.KekId, e.Key, e.Retired);
        }
    }

    /// <summary>Stores a copy of the key, evicting (and zeroing) the least recently used entry when full.</summary>
    public void Add(ColumnDek dek)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveLocked(dek.DekId);
            var node = new LinkedListNode<Entry>(new Entry(dek.DekId, dek.Scope, dek.KekId, dek.Retired, dek.Key.ToArray()));
            _order.AddFirst(node);
            _map[dek.DekId] = node;
            while (_map.Count > _capacity)
                RemoveLocked(_order.Last!.Value.Id);
        }
    }

    public void Remove(Guid dekId)
    {
        lock (_gate)
            RemoveLocked(dekId);
    }

    /// <summary>The live key arrays, so a test can check they are zeroed after eviction or dispose.</summary>
    internal IReadOnlyList<byte[]> KeyArraysForTests()
    {
        lock (_gate)
            return [.. _order.Select(e => e.Key)];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _order)
                CryptographicOperations.ZeroMemory(entry.Key);
            _order.Clear();
            _map.Clear();
        }
    }

    private void RemoveLocked(Guid dekId)
    {
        if (!_map.Remove(dekId, out var node))
            return;
        _order.Remove(node);
        CryptographicOperations.ZeroMemory(node.Value.Key);
    }
}
