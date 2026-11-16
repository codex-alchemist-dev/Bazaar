namespace Bazaar.Diagnostics.Capture;

/// <summary>
/// A fixed-capacity buffer that keeps the most recent items. When full, the oldest item is dropped and
/// <see cref="DroppedCount"/> goes up. Safe to call from any thread.
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly object _gate = new();
    private int _start;
    private int _count;
    private long _dropped;

    /// <summary>Creates an empty buffer.</summary>
    /// <param name="capacity">The number of items kept. Must be at least 1.</param>
    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    /// <summary>The number of items kept.</summary>
    public int Capacity => _items.Length;

    /// <summary>The number of items currently held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The number of items dropped because the buffer was full, since it was created.</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>Adds an item, dropping the oldest one when the buffer is full.</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
                return;
            }

            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
            _dropped++;
        }
    }

    /// <summary>A copy of the held items, oldest first. Later adds do not change it.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _items[(_start + i) % _items.Length];
            }

            return result;
        }
    }
}
