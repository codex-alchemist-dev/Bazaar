namespace Bazaar.Server.Transport.Grpc;

/// <summary>Settings of the gRPC transport. The connection layer fills them from doc 90 section 8 settings.</summary>
public sealed class GrpcTransportOptions
{
    /// <summary>Largest frame in bytes, checked on the message size before any decode (<c>wire.max_frame_kib</c>; doc 02 default 256 KiB).</summary>
    public int MaxFrameBytes { get; set; } = 256 * 1024;

    /// <summary>Codecs the operator enabled (<c>wire.codecs.enabled</c> intersected with the installed ones). The floor codec is first.</summary>
    public IReadOnlyList<string> EnabledCodecs { get; set; } = ["json"];

    /// <summary>The codec used when the client names none (Mode B in doc 90 section 4.2: the floor).</summary>
    public string FloorCodec { get; set; } = "json";

    /// <summary>The URL clients should use for the <c>grpc</c> offer, or null to advertise none.</summary>
    public string? PublicEndpoint { get; set; }

    /// <summary>Frames a gRPC-Web <c>Send</c> may queue ahead of the connection layer before it waits.</summary>
    public int WebInboundQueueDepth { get; set; } = 64;

    /// <summary>The gRPC message size limit: the frame plus the protobuf field header.</summary>
    internal int MaxMessageBytes => checked(MaxFrameBytes + 16);
}
