using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Adapters;

/// <summary>
/// Translates one wire family to and from <see cref="ModelEvent"/>. An adapter never throws for an upstream problem:
/// HTTP errors, rate limits, timeouts and dropped streams surface as a <see cref="ModelEvent.Failure"/> event.
/// Model listing, probing and rate-limit observation are added by later work packages.
/// </summary>
public interface IModelAdapter
{
    AdapterKind Kind { get; }

    IAsyncEnumerable<ModelEvent> StreamAsync(ProviderEndpoint endpoint, ModelRequest request, CancellationToken ct);
}
