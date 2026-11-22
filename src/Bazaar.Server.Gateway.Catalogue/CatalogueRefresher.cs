using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// Applies the diff between a provider's own model list and the stored rows (docs/design/05-model-gateway.md): a vanished model
/// becomes <c>missing</c> (routes skip it), a returned one becomes <c>available</c> again, a listed one gets a new <c>last_seen</c>.
/// A model that is new upstream is inserted as <c>available</c> with the family, identity and kind of the provider's most
/// recently seen existing model; a provider with no model to copy from is left alone and the ids are reported as unclassified.
/// No whitelist or route is edited, and a failed list call changes nothing.
/// </summary>
public sealed class CatalogueRefresher(
    ICatalogueStore store,
    IProviderModelLister lister,
    TimeProvider? time = null) : ICatalogueRefresher
{
    private const string _source = "catalogue";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<CatalogueRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var providers = await store.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<CatalogueRefreshResult>(providers.Count);
        foreach (var provider in providers.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            results.Add(await RefreshAsync(provider, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<CatalogueRefreshResult> RefreshProviderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var providers = await store.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));
        return provider is null
            ? new CatalogueRefreshResult(providerId, CatalogueRefreshOutcome.Skipped, Empty(), [])
            : await RefreshAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CatalogueRefreshResult> RefreshAsync(GwProvider provider, CancellationToken cancellationToken)
    {
        if (!provider.Enabled)
        {
            return new CatalogueRefreshResult(provider.Id, CatalogueRefreshOutcome.Skipped, Empty(), []);
        }

        var listed = await lister.ListModelIdsAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (listed is null)
        {
            return new CatalogueRefreshResult(provider.Id, CatalogueRefreshOutcome.ListFailed, Empty(), []);
        }

        var known = await store.ListModelsAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        var diff = CatalogueDiffer.Diff(known, listed);
        var now = _time.GetUtcNow();
        var byId = known.ToDictionary(m => m.ModelId, StringComparer.Ordinal);

        foreach (var id in diff.Missing)
        {
            await store.UpsertModelAsync(byId[id] with { Status = GwModel.StatusMissing }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in diff.Returned.Concat(diff.Unchanged))
        {
            await store.UpsertModelAsync(byId[id] with { Status = GwModel.StatusAvailable, LastSeen = now }, cancellationToken).ConfigureAwait(false);
        }

        var unclassified = new List<string>();
        var template = known.OrderByDescending(m => m.LastSeen).FirstOrDefault();
        foreach (var id in diff.Added)
        {
            if (template is null)
            {
                unclassified.Add(id);
                continue;
            }

            await store.UpsertModelAsync(
                new GwModel(provider.Id, id, template.Family, template.Identity, "{}", GwModel.StatusAvailable, now, now, _source, GwModel.KindChat),
                cancellationToken).ConfigureAwait(false);
        }

        return new CatalogueRefreshResult(provider.Id, CatalogueRefreshOutcome.Refreshed, diff, unclassified);
    }

    private static CatalogueDiff Empty() => new([], [], [], []);
}
