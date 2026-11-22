namespace Bazaar.Server.VmMounts;

/// <summary>
/// One lock per VM id, so attach, detach and write-back on one VM never overlap while other VMs run in parallel. Entries are
/// reference counted and removed when the last user leaves. Waiting honours the cancellation token.
/// </summary>
internal sealed class VmLocks
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public async Task<Lease> AcquireAsync(Guid vmId, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(vmId, out var existing))
            {
                existing = new Entry();
                _entries[vmId] = existing;
            }
            existing.Users++;
            entry = existing;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Leave(vmId, entry);
            throw;
        }

        return new Lease(this, vmId, entry);
    }

    private void Leave(Guid vmId, Entry entry)
    {
        lock (_gate)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                _entries.Remove(vmId);
                entry.Dispose();
            }
        }
    }

    private sealed class Entry : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>Holders and waiters. Guarded by the owner's gate.</summary>
        public int Users;

        public void Dispose() => Semaphore.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private readonly VmLocks _owner;
        private readonly Guid _vmId;
        private readonly Entry _entry;
        private int _released;

        internal Lease(VmLocks owner, Guid vmId, object entry)
        {
            _owner = owner;
            _vmId = vmId;
            _entry = (Entry)entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _entry.Semaphore.Release();
            _owner.Leave(_vmId, _entry);
        }
    }
}
