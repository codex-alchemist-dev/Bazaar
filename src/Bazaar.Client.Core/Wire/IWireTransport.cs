namespace Bazaar.Client.Core.Wire;

/// <summary>Opens connections to the server. The WebSocket implementation lives in a head; tests use a fake.</summary>
public interface IWireTransportFactory
{
    /// <summary>Opens one connection. Throws when the server cannot be reached.</summary>
    ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>One live socket. Frames are whole messages in the negotiated codec.</summary>
public interface IWireConnection : IAsyncDisposable
{
    /// <summary>Sends one frame.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>Waits for the next frame or for the socket to close.</summary>
    ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>A received frame, or the close of the socket when <see cref="Frame"/> is empty.</summary>
/// <param name="Frame">The frame bytes; empty when closed.</param>
/// <param name="CloseCode">The close code when the socket closed, such as 4426; 0 when no code arrived.</param>
public readonly record struct WireReceive(ReadOnlyMemory<byte> Frame, int? CloseCode)
{
    /// <summary>True when the socket is closed.</summary>
    public bool IsClosed => CloseCode is not null;

    /// <summary>A frame.</summary>
    public static WireReceive Data(ReadOnlyMemory<byte> frame) => new(frame, null);

    /// <summary>A close.</summary>
    public static WireReceive Closed(int code = 0) => new(default, code);
}
