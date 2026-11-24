using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>The provider and model rows the refresher reads and writes. The Postgres implementation wraps the gateway directory repository.</summary>
public interface ICatalogueStore
{
    Task<IReadOnlyList<GwProvider>> ListProvidersAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GwModel>> ListModelsAsync(string providerId, CancellationToken cancellationToken);

    Task UpsertModelAsync(GwModel model, CancellationToken cancellationToken);
}

/// <summary>Store over <see cref="GatewayDirectoryRepository"/>.</summary>
public sealed class GatewayCatalogueStore(GatewayDirectoryRepository repository) : ICatalogueStore
{
    public Task<IReadOnlyList<GwProvider>> ListProvidersAsync(CancellationToken cancellationToken) => repository.ListProvidersAsync(cancellationToken);

    public Task<IReadOnlyList<GwModel>> ListModelsAsync(string providerId, CancellationToken cancellationToken) => repository.ListModelsAsync(providerId, cancellationToken);

    public Task UpsertModelAsync(GwModel model, CancellationToken cancellationToken) => repository.UpsertModelAsync(model, cancellationToken);
}
