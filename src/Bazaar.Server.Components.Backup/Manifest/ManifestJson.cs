using System.Text.Json.Serialization;

namespace Bazaar.Server.Backup.Manifest;

/// <summary>Wire shape of a manifest file. Read and written only through <see cref="ManifestJsonContext"/>.</summary>
internal sealed class ManifestDocumentDto
{
    public string? Format { get; set; }

    public int Version { get; set; }

    public string? HashAlgorithm { get; set; }

    public List<ManifestEntryDto>? Entries { get; set; }
}

/// <summary>One manifest entry on the wire.</summary>
internal sealed class ManifestEntryDto
{
    public string? Path { get; set; }

    public long Length { get; set; }

    public string? Sha256 { get; set; }
}

/// <summary>
/// Source-generated System.Text.Json context for the manifest file (project wire decision: System.Text.Json, no Protobuf).
/// Unknown members are rejected so a manifest cannot carry data this build does not check.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ManifestDocumentDto))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}
