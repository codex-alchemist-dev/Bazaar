namespace Bazaar.Server.Transport.Grpc;

/// <summary>Metadata and trailer keys of the gRPC transport (doc 90 section 9). Keys are lower case, as gRPC requires.</summary>
public static class GrpcMetadata
{
    /// <summary>Request metadata: the credential, <c>Bearer &lt;token&gt;</c>. Never logged.</summary>
    public const string Authorization = "authorization";

    /// <summary>Request metadata: the codec names the client can use, comma separated, preferred first. Response header: the chosen codec.</summary>
    public const string Codec = "bazaar-codec";

    /// <summary>Request metadata, optional: the device id the client announces.</summary>
    public const string Device = "bazaar-device";

    /// <summary>Request metadata on <c>Send</c> (gRPC-Web): the session id the <c>Subscribe</c> call opened.</summary>
    public const string Session = "bazaar-session";

    /// <summary>Trailer: the wire close code (doc 02, such as 4401) when the server closed the connection with one.</summary>
    public const string CloseCode = "bazaar-close-code";

    /// <summary>Prefix of a <c>content-type</c> codec suffix, as in <c>application/grpc+cbor</c>.</summary>
    internal const string ContentTypePrefix = "application/grpc";
}
