using System.Threading.Channels;

namespace Bazaar.Client.Calcinato.Wire;

/// <summary>One body received on a named stream, such as <c>pane:&lt;terminalId&gt;</c>.</summary>
/// <param name="Stream">The stream name the body arrived on.</param>
/// <param name="Body">The raw body bytes, in the connection's codec. Not yet decoded.</param>
public sealed record PaneStreamBody(string Stream, ReadOnlyMemory<byte> Body);

/// <summary>
/// The part of the socket connection the pane hub needs: subscribe and unsubscribe to a stream, send one body on a
/// stream, and receive bodies from every subscribed stream over one connection. The real socket adapter implements
/// it in the head; the module's tests use a fake (doc 55, section 6).
/// </summary>
public interface IPaneStreamConnection
{
    /// <summary>
    /// Bodies from every subscribed stream, in arrival order. Completes when the connection closes; a reconnected
    /// connection provides a new reader.
    /// </summary>
    ChannelReader<PaneStreamBody> Bodies { get; }

    /// <summary>Subscribes this connection to a stream.</summary>
    ValueTask SubscribeAsync(string stream, CancellationToken ct);

    /// <summary>Removes the subscription for a stream.</summary>
    ValueTask UnsubscribeAsync(string stream, CancellationToken ct);

    /// <summary>Sends one body on a stream.</summary>
    ValueTask SendAsync(string stream, ReadOnlyMemory<byte> body, CancellationToken ct);
}
