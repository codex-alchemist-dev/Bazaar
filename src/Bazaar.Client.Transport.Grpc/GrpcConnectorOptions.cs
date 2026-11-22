namespace Bazaar.Client.Transport.Grpc;

/// <summary>Settings of the gRPC connector. The client head fills them; the connection layer owns the credential.</summary>
public sealed class GrpcConnectorOptions
{
    /// <summary>Supplies the bearer credential for each connect; null sends none (the server then refuses). Never logged.</summary>
    public Func<CancellationToken, ValueTask<string?>>? CredentialProvider { get; set; }

    /// <summary>The device id sent as <c>bazaar-device</c>, or null.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Largest frame in bytes in either direction (<c>wire.max_frame_kib</c>, default 256 KiB).</summary>
    public int MaxFrameBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Asks for HTTP/3 and falls back to HTTP/2 when the handler or server cannot. HTTP/2 is the default and the
    /// Android floor (doc 90 section 9); the Cronet module reuses the same framing over its own transport.
    /// </summary>
    public bool PreferHttp3 { get; set; }

    /// <summary>Makes the handler for each connection (a platform handler, a test server handler); null uses <see cref="SocketsHttpHandler"/>. The channel disposes it.</summary>
    public Func<HttpMessageHandler>? HttpHandlerFactory { get; set; }

    /// <summary>The gRPC message size limit: the frame plus the protobuf field header.</summary>
    internal int MaxMessageBytes => checked(MaxFrameBytes + 16);
}
