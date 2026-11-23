namespace Bazaar.Server.Setup.Probes;

/// <summary>Runs one probe read under a time limit. A probe never waits longer than its limit, so verification cannot hang on a dead service.</summary>
internal static class Bounded
{
    /// <summary>The default limit for one read of a service.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(10);

    /// <summary>Throws <see cref="TimeoutException"/> when the limit passes. A cancel from <paramref name="cancellationToken"/> still throws <see cref="OperationCanceledException"/>.</summary>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(limit);
        try
        {
            return await work(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }
}
