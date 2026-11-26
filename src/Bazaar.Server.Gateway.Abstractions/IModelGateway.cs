using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway;

/// <summary>The single path every agent model call takes (docs/design/05-model-gateway.md).</summary>
public interface IModelGateway
{
    IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken);
}
