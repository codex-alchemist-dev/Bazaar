using System.Text.Json.Nodes;
using Bazaar.Server.Vault.Data;
using Bazaar.Server.Vault.Links;
using Bazaar.Server.Vault.Markdown;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Vault.Index;

/// <summary>
/// A Vault file as the index stores it, before links are resolved. <see cref="LinkOrdinals"/> gives the block ordinal
/// of each entry in <see cref="Links"/>, in the same order.
/// </summary>
public sealed record ParsedVaultDocument(
    VaultDocRecord Doc,
    IReadOnlyList<VaultBlockRecord> Blocks,
    VaultIndexEntry Entry,
    IReadOnlyList<WikiLink> Links,
    IReadOnlyList<int> LinkOrdinals);

/// <summary>
/// Turns one Markdown file into index rows. A file without a front matter block is not a Vault document (doc 18 requires
/// front matter on every Vault document), so it is not indexed.
/// </summary>
public static class VaultDocumentParser
{
    private const string _markdownExtension = ".md";

    public static bool IsMarkdown(string path) =>
        path.EndsWith(_markdownExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <paramref name="source"/> stored at <paramref name="path"/>. Returns null when the file has no front matter.
    /// Throws <see cref="VaultFormatException"/> when the front matter is present but invalid.
    /// </summary>
    public static ParsedVaultDocument? TryParse(
        Guid vaultId,
        Guid docId,
        string path,
        string source,
        string headCommit,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var parsed = VaultMarkdown.Parse(source);
        if (parsed.FrontMatter is null)
        {
            return null;
        }

        var frontMatter = parsed.FrontMatter;
        if (!VaultOwner.IsWellFormed(frontMatter.Owner))
        {
            // Fail closed: a malformed owner would never match its agent, so the document is left out of the index
            // and reported as invalid, rather than being indexed as if it were shared.
            throw new VaultFormatException($"Front matter 'owner' must be 'agent:<guid>' or absent, got '{frontMatter.Owner}'.");
        }

        var blocks = VaultBlocks.Split(source);
        var blockRecords = blocks
            .Select(block => new VaultBlockRecord(
                block.Ordinal,
                block.BlockId,
                block.HeadingPath,
                block.StartLine,
                block.EndLine,
                block.TextHash,
                block.Body))
            .ToList();

        var linkOrdinals = parsed.Links
            .Select(link => Math.Max(VaultBlocks.OrdinalAt(blocks, link.Start), 0))
            .ToList();

        var title = frontMatter.Title;
        var slash = path.LastIndexOf('/');
        var area = slash < 0 ? "root" : path[..slash].Split('/')[0];
        var doc = new VaultDocRecord(
            docId,
            vaultId,
            path,
            title,
            frontMatter.Type,
            frontMatter.Project,
            area,
            frontMatter.Owner,
            FrontMatterJson(frontMatter),
            headCommit,
            updatedAt);

        var entry = new VaultIndexEntry(
            docId,
            path,
            title,
            frontMatter.Aliases,
            frontMatter.Project,
            HeadingSlugs(blocks),
            blocks.Where(block => block.BlockId is not null).Select(block => block.BlockId!).ToArray());

        return new ParsedVaultDocument(doc, blockRecords, entry, parsed.Links, linkOrdinals);
    }

    /// <summary>
    /// Heading slugs for the resolver, derived the same way from parsed blocks and from stored rows. Every heading is a
    /// block whose heading path ends in itself, so the last element of each path covers every heading.
    /// </summary>
    public static IReadOnlyList<string> HeadingSlugs(IEnumerable<VaultBlock> blocks) =>
        blocks.Where(block => block.HeadingPath.Count > 0)
            .Select(block => VaultSlug.Heading(block.HeadingPath[^1]))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string FrontMatterJson(VaultFrontMatter frontMatter)
    {
        var aliases = new JsonArray();
        foreach (var alias in frontMatter.Aliases)
        {
            aliases.Add(alias);
        }

        var json = new JsonObject
        {
            ["id"] = frontMatter.Id,
            ["type"] = frontMatter.Type,
            ["title"] = frontMatter.Title,
            ["project"] = frontMatter.Project,
            ["status"] = frontMatter.Status,
            ["aliases"] = aliases,
            ["owner"] = frontMatter.Owner,
            ["sourceTrust"] = frontMatter.SourceTrust,
            ["tier"] = frontMatter.Tier,
        };
        return json.ToJsonString();
    }
}
