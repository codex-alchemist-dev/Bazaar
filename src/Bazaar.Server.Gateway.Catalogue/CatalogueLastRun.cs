namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>The result and time of the last refresh of every provider, kept in memory only (it is gone after a restart).</summary>
public sealed class CatalogueLastRun
{
    private readonly object _gate = new();
    private (DateTimeOffset RanAt, IReadOnlyList<CatalogueRefreshResult> Results)? _last;

    public void Record(DateTimeOffset ranAt, IReadOnlyList<CatalogueRefreshResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        lock (_gate)
        {
            _last = (ranAt, results);
        }
    }

    public (DateTimeOffset RanAt, IReadOnlyList<CatalogueRefreshResult> Results)? Get()
    {
        lock (_gate)
        {
            return _last;
        }
    }
}

/// <summary>Refreshes through the inner refresher and remembers the all-provider result, so scheduled and manual runs both show as "last run".</summary>
public sealed class RecordingCatalogueRefresher(ICatalogueRefresher inner, CatalogueLastRun last, TimeProvider? time = null) : ICatalogueRefresher
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<CatalogueRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var results = await inner.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        last.Record(_time.GetUtcNow(), results);
        return results;
    }

    public Task<CatalogueRefreshResult> RefreshProviderAsync(string providerId, CancellationToken cancellationToken = default) =>
        inner.RefreshProviderAsync(providerId, cancellationToken);
}
