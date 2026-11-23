namespace Bazaar.Server.Memory.Tools;

/// <summary>A thread-safe in-process access log. The durable log replaces it when the Postgres tables exist.</summary>
public sealed class InMemoryMemoryAccessLog : IMemoryAccessLog
{
    private readonly object _sync = new();
    private readonly List<MemoryAccessEntry> _entries = [];

    /// <inheritdoc />
    public void Record(MemoryAccessEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>A snapshot of every entry, in the order recorded.</summary>
    public IReadOnlyList<MemoryAccessEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.ToArray();
            }
        }
    }
}
