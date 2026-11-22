using System.Globalization;

namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>Metadata and trailer keys; the same names as the <c>grpc</c> module (doc 90 section 9).</summary>
internal static class GrpcMetadata
{
    public const string Authorization = "authorization";
    public const string Codec = "bazaar-codec";
    public const string Device = "bazaar-device";
    public const string CloseCode = "bazaar-close-code";
    public const string Status = "grpc-status";
    public const string MethodPath = "/bazaar.wire.v1.Session/Connect";

    /// <summary>Close code for a gRPC status when the server sent no <see cref="CloseCode"/>: ResourceExhausted 4400, Unauthenticated 4401, else 0.</summary>
    public static int CloseCodeOfStatus(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(Status, out var text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var status)) return 0;
        return status switch { 8 => 4400, 16 => 4401, _ => 0 };
    }

    public static bool IsNonZeroStatus(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue(Status, out var text) && text != "0";
}
