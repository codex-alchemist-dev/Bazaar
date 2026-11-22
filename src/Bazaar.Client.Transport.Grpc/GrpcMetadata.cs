namespace Bazaar.Client.Transport.Grpc;

/// <summary>Metadata and trailer keys of the gRPC transport; the same names as the server module (doc 90 section 9).</summary>
internal static class GrpcMetadata
{
    public const string Authorization = "authorization";
    public const string Codec = "bazaar-codec";
    public const string Device = "bazaar-device";
    public const string CloseCode = "bazaar-close-code";
}
