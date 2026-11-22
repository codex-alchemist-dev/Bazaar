namespace Bazaar.Server.Wire.Connection;

/// <summary>
/// The live sockets, read-only, for features that push their own events to the connections that should see them (a reaction,
/// a typing signal). A feature checks <see cref="IWireConnection.Subscriptions"/> for the stream it targets, so the stream's
/// access rules already applied when the connection subscribed.
/// </summary>
public interface IWireConnectionDirectory
{
    /// <summary>A snapshot of the live connections.</summary>
    IReadOnlyCollection<IWireConnection> Connections { get; }
}
