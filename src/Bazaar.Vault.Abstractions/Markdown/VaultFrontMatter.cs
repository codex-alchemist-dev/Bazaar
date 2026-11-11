namespace Bazaar.Server.Vault.Markdown;

/// <summary>
/// The typed YAML front matter of a Vault document (doc 18, "Front matter").
/// <see cref="Type"/> is one of the document types in <see cref="KnownTypes"/>.
/// </summary>
public sealed record VaultFrontMatter(
    string Id,
    string Type,
    string Title,
    string? Project,
    string? Status,
    IReadOnlyList<string> Aliases,
    string? Owner,
    string? SourceTrust,
    string? Tier)
{
    /// <summary>The document types allowed by doc 18.</summary>
    public static IReadOnlySet<string> KnownTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "index", "plan", "deep-dive", "research", "constitution", "decision", "archive",
        "memory", "summary", "twin", "charter", "draft",
    };
}
