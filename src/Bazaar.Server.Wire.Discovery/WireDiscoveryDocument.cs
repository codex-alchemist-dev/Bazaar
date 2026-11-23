using System.Text.Json.Serialization;

namespace Bazaar.Server.Wire.Discovery;

/// <summary>The body of <c>GET /.well-known/bazaar/wire</c>. Public data only: no user, device, host or secret.</summary>
/// <param name="Protocol">Protocol version this build speaks.</param>
/// <param name="SchemaHash">Schema hash, <c>sha256:</c> and 64 hex digits.</param>
/// <param name="Transports">Enabled transport ids.</param>
/// <param name="Codecs">Enabled codec ids.</param>
/// <param name="Schemas">Published schema artifacts.</param>
public sealed record WireDiscoveryDocument(
    WireDiscoveryProtocol Protocol,
    string SchemaHash,
    IReadOnlyList<string> Transports,
    IReadOnlyList<string> Codecs,
    IReadOnlyList<WireDiscoverySchema> Schemas);

/// <summary>Protocol version.</summary>
/// <param name="Major">Major version.</param>
/// <param name="Minor">Minor version.</param>
public sealed record WireDiscoveryProtocol(int Major, int Minor);

/// <summary>One published schema artifact.</summary>
/// <param name="Name">Published name, such as <c>wire.cddl</c> or <c>vectors/cbor/Hello.bin</c>.</param>
/// <param name="Sha256">Hash of the bytes, <c>sha256:</c> and 64 hex digits.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="ContentType">Media type it is served with.</param>
/// <param name="Url">Route-relative URL to fetch it from.</param>
public sealed record WireDiscoverySchema(string Name, string Sha256, int Size, string ContentType, string Url);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(WireDiscoveryDocument))]
internal sealed partial class WireDiscoveryJson : JsonSerializerContext;
