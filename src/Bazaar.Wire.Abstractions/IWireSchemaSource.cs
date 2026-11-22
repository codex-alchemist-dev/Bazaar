namespace Bazaar.Wire.Abstractions;

/// <summary>The protocol version and schema hash a build publishes (doc 90 section 5).</summary>
/// <param name="Major">Protocol major version.</param>
/// <param name="Minor">Protocol minor version.</param>
/// <param name="Hash">Schema hash, <c>sha256:</c> followed by 64 lowercase hex digits. Also the <c>hash</c> field of <c>schema.json</c>.</param>
public sealed record WireSchemaInfo(int Major, int Minor, string Hash);

/// <summary>One published schema artifact: <c>schema.json</c>, <c>json-schema.json</c>, <c>wire.cddl</c> or a golden frame under <c>vectors/</c>.</summary>
/// <param name="Name">Published path, relative, with forward slashes, such as <c>vectors/cbor/Hello.bin</c>.</param>
/// <param name="ContentType">Media type to serve it with.</param>
/// <param name="Content">The bytes. Immutable for the life of the process.</param>
public sealed record WireSchemaArtifact(string Name, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>
/// Read-only access to the schema artifacts this build ships. The discovery route (doc 90 section 6) serves them without
/// knowing where they come from. Implementations hold no per-user data and no secrets.
/// </summary>
public interface IWireSchemaSource
{
    /// <summary>Protocol version and schema hash.</summary>
    WireSchemaInfo Info { get; }

    /// <summary>Every published artifact name, in a stable order.</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>Finds an artifact by its published name; false when there is none.</summary>
    bool TryGet(string name, out WireSchemaArtifact artifact);
}
