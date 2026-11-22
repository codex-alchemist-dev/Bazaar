using System.Text.Json.Serialization;

namespace Bazaar.Server.Setup;

/// <summary>
/// One template version an operator lists in the git template catalog file (doc 45 section 1.8). It pins the exact commit and the
/// template hash, so neither a moved branch nor a rewritten repository can change what a plan applies. It holds no secret: a URL
/// with credentials is refused, and private-repository access comes from the operator's own git or ssh setup on the server.
/// </summary>
public sealed class GitTemplateEntry
{
    /// <summary>The template id, as in <c>template:&lt;id&gt;@&lt;semver&gt;</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>The exact semantic version. Ranges are not supported.</summary>
    public string Version { get; init; } = "";

    /// <summary>A plain git URL: https or ssh, or file when the operator allows it. No credentials, query or fragment.</summary>
    public string Url { get; init; } = "";

    /// <summary>The full commit id in lower-case hex (40, or 64 for SHA-256). A branch or tag name is refused.</summary>
    public string Commit { get; init; } = "";

    /// <summary>The template folder inside the repository, forward-slash and relative. Empty means the repository root.</summary>
    public string Path { get; init; } = "";

    /// <summary>The expected template hash (<c>TemplateHash</c>, 64 lower-case hex). A template whose content hashes differently is refused.</summary>
    public string TemplateHash { get; init; } = "";
}

/// <summary>The catalog file: a schema version and the template versions it lists.</summary>
public sealed class GitTemplateCatalogFile
{
    /// <summary>The only schema version this build reads.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The file's schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>The listed template versions.</summary>
    public List<GitTemplateEntry> Templates { get; init; } = [];
}

/// <summary>Source-generated JSON for the git template catalog file. Unknown keys are an error.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = false)]
[JsonSerializable(typeof(GitTemplateCatalogFile))]
public sealed partial class GitTemplateCatalogJsonContext : JsonSerializerContext
{
}

/// <summary>
/// A catalog that pins the template hash of each version it holds. When the resolver's catalog is one, the loaded template's hash must
/// equal the pinned one, or the template is refused.
/// </summary>
public interface IPinnedTemplateCatalog : ITemplateCatalog
{
    /// <summary>The pinned template hash for exactly this id and version, or null when none is pinned.</summary>
    string? GetPinnedHash(string templateId, string version);
}

/// <summary>
/// Thrown by a catalog when it holds the entry but cannot produce its files (for example a failed fetch). <see cref="Exception.Message"/>
/// is safe text for the operator: it never holds a URL or a secret.
/// </summary>
public sealed class TemplateSourceException : Exception
{
    /// <param name="message">Fixed, operator-safe text.</param>
    public TemplateSourceException(string message)
        : base(message)
    {
    }
}
