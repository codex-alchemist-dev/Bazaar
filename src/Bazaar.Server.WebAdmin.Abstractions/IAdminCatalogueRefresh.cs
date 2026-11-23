namespace Bazaar.Server.WebAdmin;

/// <summary>
/// One provider's catalogue refresh as the panel shows it (docs/design/05-model-gateway.md, catalogue refresh). Counts and ids of
/// models only: no key, base URL, header or response body is ever part of it. <see cref="Outcome"/> is <c>refreshed</c>,
/// <c>listFailed</c> or <c>skipped</c>. <see cref="UnclassifiedIds"/> are new upstream models that were not inserted because the
/// provider has no existing model to take a family from.
/// </summary>
public sealed record AdminCatalogueProviderResult(
    string ProviderId,
    string Outcome,
    int Added,
    int Missing,
    int Returned,
    IReadOnlyList<string> UnclassifiedIds);

/// <summary>The last catalogue refresh. <see cref="RanAt"/> is null and <see cref="Providers"/> empty when none has run since the server started.</summary>
public sealed record AdminCatalogueReport(DateTimeOffset? RanAt, IReadOnlyList<AdminCatalogueProviderResult> Providers);

/// <summary>
/// The "check for newer catalogue" port (doc 51 section 5, gateway catalogue routes), implemented by the gateway catalogue module.
/// A refresh never edits an agent's policy or whitelist.
/// </summary>
public interface IAdminCatalogueRefresh
{
    /// <summary>Refreshes every enabled provider once and returns the per-provider result.</summary>
    Task<AdminCatalogueReport> RefreshAsync(Guid actorPrincipalId, CancellationToken ct = default);

    /// <summary>The result and time of the last refresh (manual or scheduled) since the server started.</summary>
    Task<AdminCatalogueReport> GetLastAsync(CancellationToken ct = default);
}
