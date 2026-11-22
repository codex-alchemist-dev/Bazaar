using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// The admin panel's "check for newer catalogue" port (docs/design/05-model-gateway.md, catalogue refresh). It maps refresh results to
/// ids and counts only, so no key, URL or provider response text reaches the panel.
/// </summary>
public sealed class CatalogueAdminPort(ICatalogueRefresher refresher, CatalogueLastRun last, TimeProvider? time = null) : IAdminCatalogueRefresh
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<AdminCatalogueReport> RefreshAsync(Guid actorPrincipalId, CancellationToken ct = default)
    {
        var results = await refresher.RefreshAllAsync(ct).ConfigureAwait(false);
        return Map(_time.GetUtcNow(), results);
    }

    public Task<AdminCatalogueReport> GetLastAsync(CancellationToken ct = default) =>
        Task.FromResult(last.Get() is { } l ? Map(l.RanAt, l.Results) : new AdminCatalogueReport(null, []));

    public static AdminCatalogueReport Map(DateTimeOffset ranAt, IReadOnlyList<CatalogueRefreshResult> results) =>
        new(ranAt, results.Select(MapOne).ToArray());

    private static AdminCatalogueProviderResult MapOne(CatalogueRefreshResult r) => new(
        r.ProviderId,
        r.Outcome switch
        {
            CatalogueRefreshOutcome.Refreshed => "refreshed",
            CatalogueRefreshOutcome.ListFailed => "listFailed",
            _ => "skipped",
        },
        r.Diff.Added.Count,
        r.Diff.Missing.Count,
        r.Diff.Returned.Count,
        r.Unclassified.ToArray());
}
