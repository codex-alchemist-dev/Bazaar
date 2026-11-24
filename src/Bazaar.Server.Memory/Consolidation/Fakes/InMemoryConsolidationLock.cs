namespace Bazaar.Server.Memory.Consolidation.Fakes;

/// <summary>In-memory <see cref="IConsolidationLock"/> for tests. Holds one lease per agent until disposed.</summary>
public sealed class InMemoryConsolidationLock : IConsolidationLock
{
    private readonly object _sync = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IDisposable?> TryAcquireAsync(string agentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ct.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_held.Add(agentId))
            {
                return Task.FromResult<IDisposable?>(null);
            }
        }

        return Task.FromResult<IDisposable?>(new Lease(this, agentId));
    }

    private void Release(string agentId)
    {
        lock (_sync)
        {
            _held.Remove(agentId);
        }
    }

    private sealed class Lease(InMemoryConsolidationLock owner, string agentId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(agentId);
            }
        }
    }
}
