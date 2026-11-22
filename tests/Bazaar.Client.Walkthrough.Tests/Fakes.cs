namespace Bazaar.Client.Walkthrough.Tests;

internal sealed class FakeStore(WalkthroughProgress? initial = null) : IWalkthroughProgressStore
{
    public WalkthroughProgress? Saved { get; private set; } = initial;
    public bool Throws { get; set; }

    public Task<WalkthroughProgress?> LoadAsync(CancellationToken cancellationToken = default) =>
        Throws ? throw new IOException("store") : Task.FromResult(Saved);

    public Task SaveAsync(WalkthroughProgress progress, CancellationToken cancellationToken = default)
    {
        if (Throws) throw new IOException("store");
        Saved = progress;
        return Task.CompletedTask;
    }
}

internal sealed class FakeFacts(WalkthroughFacts facts) : IWalkthroughFactsSource
{
    public WalkthroughFacts Facts { get; set; } = facts;
    public bool Throws { get; set; }

    public Task<WalkthroughFacts> ReadAsync(CancellationToken cancellationToken = default) =>
        Throws ? throw new HttpRequestException("down") : Task.FromResult(Facts);
}

internal sealed class FakeActions : IWalkthroughActions
{
    public List<WalkthroughStep> Opened { get; } = [];
    public bool Result { get; set; } = true;

    public Task<bool> OpenAsync(WalkthroughStep which, CancellationToken cancellationToken = default)
    {
        Opened.Add(which);
        return Task.FromResult(Result);
    }
}
