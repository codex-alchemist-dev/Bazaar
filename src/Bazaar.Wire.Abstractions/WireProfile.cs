namespace Bazaar.Wire.Abstractions;

/// <summary>The triple chosen for one connection (doc 90 section 1).</summary>
/// <param name="Transport">Transport id, such as <c>ws</c>, <c>quic</c>, <c>grpc</c>.</param>
/// <param name="Codec">Codec name, such as <c>json</c>.</param>
/// <param name="Transform">Optional transform name, such as <c>zstd</c>; null when none.</param>
public readonly record struct WireProfile(string Transport, string Codec, string? Transform = null)
{
    /// <summary>The floor every install keeps: WebSocket and JSON.</summary>
    public static WireProfile Floor { get; } = new(WireNames.WebSocket, WireNames.Json);
}

/// <summary>One way to reach the server, as the server advertises it (doc 90 section 4.3).</summary>
/// <param name="Transport">Transport id.</param>
/// <param name="Endpoint">URL or host:port the client connects to; no secret.</param>
/// <param name="Alpn">ALPN or subprotocol family, or null.</param>
/// <param name="Codecs">Codec names usable over this offer, in server preference order.</param>
public sealed record TransportOffer(string Transport, string Endpoint, string? Alpn, IReadOnlyList<string> Codecs);

/// <summary>Well-known transport and codec ids.</summary>
public static class WireNames
{
    /// <summary>WebSocket transport.</summary>
    public const string WebSocket = "ws";

    /// <summary>Native QUIC transport.</summary>
    public const string Quic = "quic";

    /// <summary>WebTransport.</summary>
    public const string WebTransport = "webtransport";

    /// <summary>gRPC.</summary>
    public const string Grpc = "grpc";

    /// <summary>gRPC-Web.</summary>
    public const string GrpcWeb = "grpc-web";

    /// <summary>JSON codec, the floor.</summary>
    public const string Json = "json";

    /// <summary>True for transports that run over UDP (QUIC based).</summary>
    public static bool IsUdpBased(string transport) =>
        transport is Quic or WebTransport;
}
