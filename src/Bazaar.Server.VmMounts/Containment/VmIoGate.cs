namespace Bazaar.Server.VmMounts.Containment;

/// <summary>
/// Cancellation scopes for guest I/O, one per VM. Every guest call (profile push or pull, provider attach) runs under the VM's
/// current scope, linked to the caller's token and a server-side timeout. A containment detach cancels the VM's scope first, so the
/// call that holds the VM lock aborts and the lock frees at once, instead of the detach queueing behind a slow or hostile guest.
/// </summary>
internal sealed class VmIoGate
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _scopes = [];
    private readonly TimeSpan _timeout;

    public VmIoGate(TimeSpan timeout) => _timeout = timeout;

    /// <summary>Runs <paramref name="work"/> under the VM's scope, the caller's token and the guest I/O timeout.</summary>
    public async Task<T> RunAsync<T>(Guid vmId, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        CancellationToken scopeToken;
        lock (_gate)
        {
            if (!_scopes.TryGetValue(vmId, out var scope))
            {
                scope = new CancellationTokenSource();
                _scopes[vmId] = scope;
            }

            // Read inside the lock: a cancelled scope is only dropped, never disposed, so this token stays valid.
            scopeToken = scope.Token;
        }

        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, scopeToken, timeout.Token);
        return await work(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Cancels every guest call now in flight for the VM. Later calls get a fresh scope.</summary>
    public void CancelAll(Guid vmId)
    {
        CancellationTokenSource? scope;
        lock (_gate)
            _scopes.Remove(vmId, out scope);
        scope?.Cancel();
    }
}
