using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.EngineRelay;

/// <summary>
/// <see cref="IRelayTargetDirectory"/> over the wire kernel's live connections. The target is the owner's Windows desktop
/// that subscribed to the relay stream. With several, the lowest device id wins so the choice is stable. None means null:
/// the call fails at once and nothing is queued.
/// </summary>
public sealed class WireRelayTargetDirectory(IWireConnectionDirectory connections) : IRelayTargetDirectory
{
    /// <inheritdoc />
    public ValueTask<string?> FindTargetDeviceAsync(CancellationToken ct)
    {
        string? device = connections.Connections
            .Where(RelayDevicePolicy.IsTarget)
            .Select(c => c.DeviceId)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        return ValueTask.FromResult(device);
    }
}
