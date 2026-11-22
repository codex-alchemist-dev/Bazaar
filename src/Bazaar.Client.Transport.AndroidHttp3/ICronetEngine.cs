using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>
/// The seam to Cronet's bidirectional stream API (doc 90 section 9). The Android head implements it over the Java
/// binding; this module stays platform-neutral and tests use an in-memory fake.
/// </summary>
public interface ICronetEngine
{
    /// <summary>False when Cronet is missing or disabled on this device; the transport then drops out of negotiation.</summary>
    bool IsAvailable { get; }

    /// <summary>Opens one bidirectional stream. Request headers are sent at once; nothing is replayable (no early data).</summary>
    ValueTask<ICronetBidirectionalStream> OpenStreamAsync(CronetStreamRequest request, CancellationToken cancellationToken);
}

/// <summary>What to open.</summary>
public sealed class CronetStreamRequest
{
    /// <summary>The HTTPS URL of the gRPC method.</summary>
    public required Uri Url { get; init; }

    /// <summary>Request headers, names lower case.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }

    /// <summary>Always false: 0-RTT and TLS early data are off, so auth-bearing frames are never replayable (doc 90 section 4.5).</summary>
    public bool AllowEarlyData { get; }
}

/// <summary>The response head of a stream.</summary>
/// <param name="HttpStatus">The HTTP status code.</param>
/// <param name="NegotiatedProtocol">The protocol Cronet negotiated: <c>h3</c>, <c>h2</c>, or another ALPN id.</param>
/// <param name="Headers">Response headers, names lower case.</param>
public sealed record CronetResponseHead(int HttpStatus, string NegotiatedProtocol, IReadOnlyDictionary<string, string> Headers);

/// <summary>One open Cronet bidirectional stream.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Named after Cronet's BidirectionalStream, which it wraps; it is not a System.IO.Stream.")]
public interface ICronetBidirectionalStream : IAsyncDisposable
{
    /// <summary>Raised on the network changing under the stream, on the device suspending it (Doze), and on resuming. May fire on any thread.</summary>
    event Action<WireNetworkEvent>? NetworkEvent;

    /// <summary>Waits for the response headers.</summary>
    ValueTask<CronetResponseHead> ReadResponseHeadAsync(CancellationToken cancellationToken);

    /// <summary>Writes request body bytes.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Reads response body bytes; 0 means the body ended and the trailers are final.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>The trailers; valid once <see cref="ReadAsync"/> returned 0, empty when the stream ended without any.</summary>
    IReadOnlyDictionary<string, string> Trailers { get; }

    /// <summary>True when the body ended with trailers (a clean end), false when it was cut.</summary>
    bool EndedCleanly { get; }

    /// <summary>Half-closes the request side.</summary>
    ValueTask CompleteWritesAsync(CancellationToken cancellationToken);
}
