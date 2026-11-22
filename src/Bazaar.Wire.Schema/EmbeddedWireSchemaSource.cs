using System.Reflection;
using Bazaar.Wire.Abstractions;
using Bazaar.Wire.Schema.Generated;

namespace Bazaar.Wire.Schema;

/// <summary>
/// Serves the schema artifacts compiled into this assembly as embedded resources (doc 90 section 5). The checked-in files under
/// <c>docs/wire</c> are the resources; the tests prove they equal what the generator builds from the payload records.
/// </summary>
public sealed class EmbeddedWireSchemaSource : IWireSchemaSource
{
    private readonly Dictionary<string, WireSchemaArtifact> _artifacts;

    /// <summary>Loads every embedded artifact.</summary>
    public EmbeddedWireSchemaSource()
    {
        Info = new WireSchemaInfo(WireSchemaArtifacts.ProtocolMajor, WireSchemaArtifacts.ProtocolMinor, WireSchemaArtifacts.SchemaHash);
        var assembly = typeof(EmbeddedWireSchemaSource).Assembly;
        _artifacts = new Dictionary<string, WireSchemaArtifact>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            var name = resource.Replace('\\', '/');
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Missing resource " + resource);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            _artifacts[name] = new WireSchemaArtifact(name, ContentTypeOf(name), buffer.ToArray());
        }

        Names = [.. _artifacts.Keys];
    }

    /// <inheritdoc />
    public WireSchemaInfo Info { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> Names { get; }

    /// <inheritdoc />
    public bool TryGet(string name, out WireSchemaArtifact artifact) => _artifacts.TryGetValue(name, out artifact!);

    private static string ContentTypeOf(string name) => Path.GetExtension(name) switch
    {
        ".json" => "application/json",
        ".cddl" => "text/plain; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };
}
