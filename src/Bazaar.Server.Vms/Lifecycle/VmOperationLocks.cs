namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// One lock per VM id. Two lifecycle operations on the same VM never overlap; operations on different VMs run in parallel.
/// Each entry is reference counted (holder plus waiters) and removed when the last user leaves, so the table does not grow
/// with the number of VMs seen. Waiting honours the cancellation token.
/// </summary>
public sealed class VmOperationLocks
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();

    /// <summary>Number of VM ids that currently have a holder or a waiter. Zero when no operation is running.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>Waits for the VM's lock. The lock is held until the returned lease is disposed.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled while waiting. The lock is not taken.</exception>
    public async Task<Lease> AcquireAsync(Guid vmId, CancellationToken ct)
    {
        var entry = Join(vmId);
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

    private Entry Join(Guid vmId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(vmId, out var entry))
            {
                entry = new Entry();
                _entries[vmId] = entry;
            }
            entry.Users++;
            return entry;
        }
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

    internal sealed class Entry : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>Holders and waiters. Guarded by the owner's gate.</summary>
        public int Users;

        public void Dispose() => Semaphore.Dispose();
    }

    /// <summary>A held VM lock. Disposing releases it; a second Dispose does nothing.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly VmOperationLocks _owner;
        private readonly Guid _vmId;
        private readonly Entry _entry;
        private int _released;

        internal Lease(VmOperationLocks owner, Guid vmId, Entry entry)
        {
            _owner = owner;
            _vmId = vmId;
            _entry = entry;
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
