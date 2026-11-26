using Bazaar.Contracts.Engines;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.EngineRelay;

/// <summary>
/// Who may receive and answer relay requests (doc 02 stream registry, doc 28): the owner's Windows desktop device, subscribed to
/// <see cref="EngineRelayWire.Stream"/>. One rule, used for finding the target, publishing and answering, so they cannot drift.
/// </summary>
internal static class RelayDevicePolicy
{
    private const string _ownerKind = "owner";
    private const string _windowsDeviceClass = "windows";

    /// <summary>True when the connection is the owner's Windows desktop and its subscription to the relay stream was accepted.</summary>
    public static bool IsTarget(IWireConnection connection) =>
        !connection.Closed.IsCancellationRequested
        && string.Equals(connection.Principal.Kind, _ownerKind, StringComparison.Ordinal)
        && string.Equals(connection.DeviceClass, _windowsDeviceClass, StringComparison.OrdinalIgnoreCase)
        && connection.Subscriptions is { } session
        && session.Streams.Contains(EngineRelayWire.Stream, StringComparer.Ordinal);
}
