using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Vault.Links;

/// <summary>
/// Pure wiki-link resolution (doc 18, "Links"). No I/O: the caller supplies the index and a read check.
/// Entries the reader cannot see are removed first, so they never match, never count as ambiguity and never appear in candidates.
/// </summary>
public static class VaultLinkResolver
{
    private const string _markdownExtension = ".md";

    public static LinkResolution Resolve(
        WikiLink link,
        VaultIndexEntry source,
        IEnumerable<VaultIndexEntry> index,
        Func<VaultIndexEntry, bool> canRead,
        VaultAmbiguousLinkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(canRead);

        // a. Hidden entries are dropped before any matching.
        var visible = index.Where(canRead).ToList();

        // b. Same-document link: the target is the source itself.
        if (string.IsNullOrEmpty(link.Target))
        {
            return WithAnchor(link, Resolved(source));
        }

        // c. Path match, with a trailing ".md" stripped from both sides.
        var linkPath = StripMarkdownExtension(link.Target);
        var pathMatches = visible.Where(entry => string.Equals(StripMarkdownExtension(entry.Path), linkPath, StringComparison.Ordinal)).ToList();
        if (pathMatches.Count == 1)
        {
            return WithAnchor(link, Resolved(pathMatches[0]));
        }

        // d. Title or alias match, case-insensitive.
        var matches = visible.Where(entry => MatchesTitleOrAlias(entry, link.Target)).ToList();
        if (matches.Count == 1)
        {
            return WithAnchor(link, Resolved(matches[0]));
        }

        if (matches.Count > 1)
        {
            var pool = matches;
            if (source.Project is not null)
            {
                var sameProject = matches.Where(entry => string.Equals(entry.Project, source.Project, StringComparison.Ordinal)).ToList();
                if (sameProject.Count == 1)
                {
                    return WithAnchor(link, Resolved(sameProject[0]));
                }

                if (sameProject.Count > 1)
                {
                    pool = sameProject;
                }
            }

            // f. Ambiguous: every remaining candidate is reported, ordered by path.
            var candidates = pool.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
            return WithAnchor(link, Ambiguous(link, candidates, policy));
        }

        // e. Nothing matched. Hidden entries are never mentioned, so the warning is null.
        return new LinkResolution(false, null, false, [], null);
    }

    private static LinkResolution Resolved(VaultIndexEntry target) =>
        new(true, target, false, [], null);

    private static LinkResolution Ambiguous(WikiLink link, List<VaultIndexEntry> candidates, VaultAmbiguousLinkPolicy policy)
    {
        if (policy == VaultAmbiguousLinkPolicy.Refuse)
        {
            return new LinkResolution(
                false,
                null,
                false,
                candidates,
                $"[[{link.Target}]] is ambiguous: {candidates.Count} notes match and the link was not resolved.");
        }

        return new LinkResolution(
            true,
            candidates[0],
            false,
            candidates,
            $"[[{link.Target}]] is ambiguous: {candidates.Count} notes match; linked to the first by path.");
    }

    // g. Anchor check, only when a target was resolved. A broken anchor keeps the link resolved.
    private static LinkResolution WithAnchor(WikiLink link, LinkResolution resolution)
    {
        if (!resolution.Resolved || resolution.Target is null)
        {
            return resolution;
        }

        var found = link.AnchorKind switch
        {
            WikiLinkAnchorKind.None => true,
            WikiLinkAnchorKind.Heading => resolution.Target.HeadingSlugs.Contains(VaultSlug.Heading(link.Anchor), StringComparer.Ordinal),
            WikiLinkAnchorKind.Block => link.Anchor is not null
                && resolution.Target.BlockIds.Contains(link.Anchor, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };

        return resolution with { AnchorFound = found };
    }

    private static bool MatchesTitleOrAlias(VaultIndexEntry entry, string target)
    {
        if (entry.Title is not null && string.Equals(entry.Title, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return entry.Aliases.Any(alias => string.Equals(alias, target, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripMarkdownExtension(string path) =>
        path.EndsWith(_markdownExtension, StringComparison.Ordinal)
            ? path[..^_markdownExtension.Length]
            : path;
}
