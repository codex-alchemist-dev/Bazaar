using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// An <see cref="IModelGateway"/> that replays one provider and model from a <see cref="CassetteStore"/>. A request with
/// no entry throws <see cref="CassetteMismatchException"/> from <see cref="CallAsync"/> itself, before any event is read.
/// </summary>
public sealed class CassetteGateway(CassetteStore store, string provider, string model) : IModelGateway
{
    private readonly CassetteStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _provider = !string.IsNullOrWhiteSpace(provider) ? provider : throw new ArgumentException("A provider name is required.", nameof(provider));
    private readonly string _model = !string.IsNullOrWhiteSpace(model) ? model : throw new ArgumentException("A model id is required.", nameof(model));

    public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var events = _store.Replay(_provider, _model, request);
        return Play(events, cancellationToken);
    }

    private static async IAsyncEnumerable<ModelEvent> Play(
        IReadOnlyList<ModelEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var modelEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return modelEvent;
        }
    }
}
