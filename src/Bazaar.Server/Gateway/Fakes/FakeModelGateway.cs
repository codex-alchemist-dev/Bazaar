using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// The gateway seam over a <see cref="FakeProvider"/>. Pass it to <see cref="Ai.GatewayChatClient"/> to exercise scripted
/// <see cref="ModelEvent"/> steps through the <c>IChatClient</c> adapter, exactly as a real gateway call would.
/// </summary>
public sealed class FakeModelGateway : IModelGateway
{
    private readonly FakeProvider _provider;

    public FakeModelGateway(FakeProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken) =>
        _provider.StreamAsync(request, cancellationToken);
}
