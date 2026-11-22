namespace Bazaar.Wire.Abstractions;

/// <summary>One live connection carrying whole frames in the negotiated codec (doc 90 section 1).</summary>
public interface IWireConnection : IAsyncDisposable
{
    /// <summary>The profile this connection runs.</summary>
    WireProfile Profile { get; }

    /// <summary>Sends one frame.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>Waits for the next frame or for the connection to close.</summary>
    ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Client side of a transport module: opens connections.</summary>
public interface IWireTransportConnector
{
    /// <summary>Transport id, such as <c>quic</c>.</summary>
    string Transport { get; }

    /// <summary>False when the platform cannot run this transport (for example no MsQuic); it then drops out of negotiation.</summary>
    bool IsSupported { get; }

    /// <summary>Opens a connection to the offer using the codecs in preference order; the chosen codec is in <see cref="IWireConnection.Profile"/>.</summary>
    ValueTask<IWireConnection> ConnectAsync(TransportOffer offer, IReadOnlyList<string> codecs, CancellationToken cancellationToken);
}

/// <summary>Server side of a transport module: accepts connections and hands them to the connection layer.</summary>
public interface IWireTransportListener
{
    /// <summary>Transport id.</summary>
    string Transport { get; }

    /// <summary>False when the platform cannot run this transport.</summary>
    bool IsSupported { get; }

    /// <summary>The offers this listener adds to discovery and <c>welcome</c>; empty when it has none to show.</summary>
    IReadOnlyList<TransportOffer> Offers { get; }
}

/// <summary>A received frame, or the close of the connection when <see cref="CloseCode"/> is set.</summary>
/// <param name="Frame">The frame bytes; empty when closed.</param>
/// <param name="CloseCode">The close code, such as 4426; 0 when closed with no code.</param>
public readonly record struct WireReceive(ReadOnlyMemory<byte> Frame, int? CloseCode)
{
    /// <summary>True when the connection is closed.</summary>
    public bool IsClosed => CloseCode is not null;

    /// <summary>A frame.</summary>
    public static WireReceive Data(ReadOnlyMemory<byte> frame) => new(frame, null);

    /// <summary>A close.</summary>
    public static WireReceive Closed(int code = 0) => new(default, code);
}
