using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.EngineRelay;

/// <summary>
/// Access rule for the <c>engine.relay</c> stream (doc 02 stream registry): only the owner may subscribe. The device-class half of the
/// rule (the owner's Windows desktop) is re-checked by <see cref="RelayDevicePolicy"/> when a target is picked, published to and
/// answered, because a stream gate sees the principal and not the device.
/// </summary>
public sealed class EngineRelayStreamGate : IStreamGate
{
    /// <inheritdoc />
    public string Stream => EngineRelayWire.Stream;

    /// <inheritdoc />
    public ValueTask<bool> CanSubscribeAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct) =>
        ValueTask.FromResult(principalKind == PrincipalKind.Owner);
}
