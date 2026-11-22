namespace Bazaar.Client.Walkthrough;

/// <summary>Keeps progress for this run only. The default until a head supplies a <c>prefs.set</c>-backed store.</summary>
public sealed class InMemoryWalkthroughProgressStore : IWalkthroughProgressStore
{
    private WalkthroughProgress? _progress;

    public Task<WalkthroughProgress?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_progress);

    public Task SaveAsync(WalkthroughProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _progress = progress;
        return Task.CompletedTask;
    }
}
