namespace Bazaar.Server.Terminal;

/// <summary>
/// A bounded ring of recent terminal output, kept as sequence-numbered chunks (doc 52, section 4). Sequence numbers start at 1
/// and never repeat. When the byte budget is exceeded the oldest chunks are dropped from replay only. Thread safe.
/// </summary>
public sealed class ScrollbackRing
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private byte[][] _chunks = new byte[16][];
    private int _head;
    private int _count;
    private int _bytes;
    private long _lastSeq;

    /// <summary>Creates a ring. The capacity is raised to <see cref="TerminalOptions.ScrollbackFloorBytes"/> when smaller.</summary>
    public ScrollbackRing(int capacityBytes)
        : this(capacityBytes, enforceFloor: true)
    {
    }

    internal ScrollbackRing(int capacityBytes, bool enforceFloor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1);
        _capacity = enforceFloor ? Math.Max(capacityBytes, TerminalOptions.ScrollbackFloorBytes) : capacityBytes;
    }

    /// <summary>The byte budget.</summary>
    public int Capacity => _capacity;

    /// <summary>Bytes currently held.</summary>
    public int ByteCount
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Chunks currently held.</summary>
    public int ChunkCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>The sequence of the newest chunk ever appended, 0 when none.</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    /// <summary>The sequence of the oldest chunk still replayable. Equals <c>LastSeq + 1</c> when the ring is empty.</summary>
    public long OldestSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq - _count + 1;
            }
        }
    }

    /// <summary>
    /// Appends a copy of <paramref name="data"/> and returns its sequence number. A chunk larger than the whole ring is not stored
    /// (it still takes a sequence number) and everything older is dropped, so replay never has a hole inside the held range.
    /// </summary>
    public long Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var seq = ++_lastSeq;
            if (data.Length > _capacity)
            {
                Clear();
                return seq;
            }

            if (_count == _chunks.Length)
            {
                Grow();
            }

            _chunks[(_head + _count) % _chunks.Length] = data.ToArray();
            _count++;
            _bytes += data.Length;
            while (_bytes > _capacity && _count > 0)
            {
                _bytes -= _chunks[_head].Length;
                _chunks[_head] = null!;
                _head = (_head + 1) % _chunks.Length;
                _count--;
            }

            return seq;
        }
    }

    /// <summary>Gets the chunk with this sequence number when it is still held.</summary>
    public bool TryGet(long seq, out ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            var oldest = _lastSeq - _count + 1;
            if (_count == 0 || seq < oldest || seq > _lastSeq)
            {
                bytes = default;
                return false;
            }

            bytes = _chunks[(_head + (int)(seq - oldest)) % _chunks.Length];
            return true;
        }
    }

    /// <summary>
    /// Returns up to <paramref name="maxChunks"/> held chunks from <paramref name="fromSeq"/>. A <paramref name="fromSeq"/> of 0 or
    /// older than the ring starts at the oldest held chunk, so the caller sees the gap in the first returned sequence.
    /// </summary>
    public IReadOnlyList<(long Seq, ReadOnlyMemory<byte> Bytes)> ReadFrom(long fromSeq, int maxChunks = int.MaxValue)
    {
        lock (_gate)
        {
            var oldest = _lastSeq - _count + 1;
            var start = Math.Max(fromSeq, oldest);
            var result = new List<(long, ReadOnlyMemory<byte>)>();
            for (var seq = start; seq <= _lastSeq && result.Count < maxChunks; seq++)
            {
                result.Add((seq, _chunks[(_head + (int)(seq - oldest)) % _chunks.Length]));
            }

            return result;
        }
    }

    private void Clear()
    {
        Array.Clear(_chunks);
        _head = 0;
        _count = 0;
        _bytes = 0;
    }

    private void Grow()
    {
        var bigger = new byte[_chunks.Length * 2][];
        for (var i = 0; i < _count; i++)
        {
            bigger[i] = _chunks[(_head + i) % _chunks.Length];
        }

        _chunks = bigger;
        _head = 0;
    }
}
