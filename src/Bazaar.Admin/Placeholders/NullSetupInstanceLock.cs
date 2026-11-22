using Bazaar.Server.Setup;

namespace Bazaar.Admin.Placeholders;

/// <summary>
/// PLACEHOLDER. Takes no lock. The server host supplies the real instance lock (an advisory lock, doc 03) and replaces this class.
/// It is safe in this build only because no setup steps are registered, so an apply cannot change anything under it.
/// </summary>
internal sealed class NullSetupInstanceLock : ISetupInstanceLock
{
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAsyncDisposable>(new Lease());
    }

    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
