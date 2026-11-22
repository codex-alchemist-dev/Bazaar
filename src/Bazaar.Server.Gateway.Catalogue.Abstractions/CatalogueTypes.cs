namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// What one catalogue refresh changed for one provider (docs/design/05-model-gateway.md, "Behaviour and edge cases"). Model ids
/// are the provider's own. <c>Added</c> are listed upstream and unknown here, <c>Missing</c> are known here and no longer listed,
/// <c>Returned</c> were <c>missing</c> and are listed again, <c>Unchanged</c> are listed and already available.
/// </summary>
public sealed record CatalogueDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Returned,
    IReadOnlyList<string> Unchanged)
{
    /// <summary>True when the refresh changes no row's status and adds no model.</summary>
    public bool IsEmpty => Added.Count == 0 && Missing.Count == 0 && Returned.Count == 0;
}

/// <summary>How a refresh of one provider ended.</summary>
public enum CatalogueRefreshOutcome
{
    /// <summary>The upstream list was read and the diff was applied.</summary>
    Refreshed,

    /// <summary>The upstream list could not be read (unreachable, refused, or an unreadable body). Nothing was changed, so a failed call never marks a model missing.</summary>
    ListFailed,

    /// <summary>The provider's adapter has no model-list reader here, or the provider is disabled. Nothing was changed.</summary>
    Skipped,
}

/// <summary>The result for one provider. <c>Unclassified</c> lists added ids that were not inserted because the provider has no existing model to take a family and identity from.</summary>
public sealed record CatalogueRefreshResult(
    string ProviderId,
    CatalogueRefreshOutcome Outcome,
    CatalogueDiff Diff,
    IReadOnlyList<string> Unclassified);

/// <summary>Reads the model ids a provider currently lists. Returns null when the list cannot be read, which is not the same as an empty list.</summary>
public interface IProviderModelLister
{
    /// <summary>The ids the provider lists, or null when the provider is unsupported by this lister or the call failed.</summary>
    Task<IReadOnlyList<string>?> ListModelIdsAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>Refreshes the model catalogue from the providers' own lists. It never edits an agent's policy: a new model joins no whitelist.</summary>
public interface ICatalogueRefresher
{
    /// <summary>Refreshes every enabled provider once (the manual "check for newer catalogue" button and the scheduled run).</summary>
    Task<IReadOnlyList<CatalogueRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes one provider once.</summary>
    Task<CatalogueRefreshResult> RefreshProviderAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>Where the scheduled refresh reads <c>gateway.catalogue.refresh_hours</c>. 0 means no scheduled refresh.</summary>
public interface ICatalogueSchedule
{
    /// <summary>The configured interval in hours, 0 when off.</summary>
    Task<int> GetRefreshHoursAsync(CancellationToken cancellationToken = default);
}
