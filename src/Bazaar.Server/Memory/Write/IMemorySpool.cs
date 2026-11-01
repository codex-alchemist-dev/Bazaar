namespace Bazaar.Server.Memory.Write;

/// <summary>
/// Durable record of queued memory writes, so a restart between enqueue and flush loses nothing.
/// The Postgres table (<c>memory_spool</c>) implements this later.
/// </summary>
public interface IMemorySpool
{
    /// <summary>Stores one write before the queue accepts it.</summary>
    Task AppendAsync(MemoryWriteItem item, CancellationToken ct);

    /// <summary>Removes writes that have been committed.</summary>
    Task RemoveAsync(IReadOnlyCollection<Guid> writeIds, CancellationToken ct);

    /// <summary>Returns every stored write in insertion order.</summary>
    Task<IReadOnlyList<MemoryWriteItem>> ReadAllAsync(CancellationToken ct);
}
