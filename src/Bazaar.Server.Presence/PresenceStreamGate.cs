using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Presence;

/// <summary>
/// Lets humans (the owner and human co-members) subscribe to the <c>presence</c> stream and keeps agents off it (docs/design/23:
/// the owner sees presence in v1). It registers with the wire layer as an <see cref="IStreamGate"/>, so Wire never references this
/// module.
/// </summary>
public sealed class PresenceStreamGate : IStreamGate
{
    /// <inheritdoc />
    public string Stream => WireStreams.Presence;

    /// <inheritdoc />
    public ValueTask<bool> CanSubscribeAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct) =>
        ValueTask.FromResult(principalKind != PrincipalKind.Agent);
}
