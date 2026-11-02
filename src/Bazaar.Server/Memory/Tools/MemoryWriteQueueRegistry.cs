using Bazaar.Server.Memory.Write;

namespace Bazaar.Server.Memory.Tools;

/// <summary>Holds one <see cref="MemoryWriteQueue"/> per agent, created on first use with shared settings.</summary>
public sealed class MemoryWriteQueueRegistry : IDisposable
{
    private readonly IMemoryBatchSink _sink;
    private readonly IMemorySpool _spool;
    private readonly TimeProvider _time;
    private readonly int _idleSeconds;
    private readonly int _maxWrites;
    private readonly object _sync = new();
    private readonly Dictionary<string, MemoryWriteQueue> _queues = new(StringComparer.Ordinal);

    /// <summary>Creates the registry.</summary>
    /// <param name="sink">Commits flushed batches.</param>
    /// <param name="spool">Durable record of accepted writes.</param>
    /// <param name="time">Clock for idle windows.</param>
    /// <param name="idleSeconds">The <c>memory.batch.idle_seconds</c> setting.</param>
    /// <param name="maxWrites">The <c>memory.batch.max_writes</c> setting.</param>
    public MemoryWriteQueueRegistry(IMemoryBatchSink sink, IMemorySpool spool, TimeProvider time, int idleSeconds = 30, int maxWrites = 20)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _idleSeconds = idleSeconds;
        _maxWrites = maxWrites;
    }

    /// <summary>The queue for an agent, created when first asked for.</summary>
    public MemoryWriteQueue For(string agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        lock (_sync)
        {
            if (!_queues.TryGetValue(agentId, out var queue))
            {
                queue = new MemoryWriteQueue(agentId, _sink, _spool, _time, _idleSeconds, _maxWrites);
                _queues[agentId] = queue;
            }

            return queue;
        }
    }

    /// <summary>Disposes every queue.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var queue in _queues.Values)
            {
                queue.Dispose();
            }

            _queues.Clear();
        }
    }
}
