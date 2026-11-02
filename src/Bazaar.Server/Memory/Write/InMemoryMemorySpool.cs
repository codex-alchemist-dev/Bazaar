namespace Bazaar.Server.Memory.Write;

/// <summary>In-memory <see cref="IMemorySpool"/> for tests. Keeps insertion order and dedupes by write id.</summary>
public sealed class InMemoryMemorySpool : IMemorySpool
{
    private readonly object _sync = new();
    private readonly List<MemoryWriteItem> _items = [];

    /// <summary>Number of stored writes.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task AppendAsync(MemoryWriteItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_items.Exists(i => i.WriteId == item.WriteId))
            {
                _items.Add(item);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(IReadOnlyCollection<Guid> writeIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writeIds);
        ct.ThrowIfCancellationRequested();

        var ids = writeIds.ToHashSet();
        lock (_sync)
        {
            _items.RemoveAll(i => ids.Contains(i.WriteId));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryWriteItem>> ReadAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<MemoryWriteItem>>(_items.ToList());
        }
    }
}
