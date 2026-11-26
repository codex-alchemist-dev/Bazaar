using System.Collections.Frozen;

namespace Bazaar.Server.Vault.Search;

/// <summary>
/// Who is asking. The owner reads every document; an agent reads <c>company/</c>, <c>shared/</c>, its own area, its own
/// private memory (<see cref="AgentOwner"/>, "agent:&lt;id&gt;") and the ownerless project areas of the projects in
/// <see cref="Projects"/>. Other agents' private memory is never returned, and other documents with no owner (<c>hive/</c>,
/// <c>decisions/</c>, drafts, <c>archive/</c>, root files) are the owner's only (doc 18, "Access").
/// </summary>
public sealed record VaultCaller(bool IsOwner, string? AgentOwner)
{
    private readonly FrozenSet<string> _projects = FrozenSet<string>.Empty;

    public static VaultCaller Owner { get; } = new(IsOwner: true, AgentOwner: null);

    /// <summary>
    /// The slugs of the projects the caller is a member of, resolved at request time from project membership (a cover
    /// carries its primary's projects). Empty by default, so a caller whose membership was not resolved reads no project
    /// area. The set is copied on assignment and cannot change afterwards. Each slug must be one non-empty path segment.
    /// </summary>
    /// <exception cref="ArgumentException">A slug is null, empty or contains a slash.</exception>
    public IReadOnlySet<string> Projects
    {
        get => _projects;
        init => _projects = CopyProjects(value);
    }

    private static FrozenSet<string> CopyProjects(IReadOnlySet<string> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        foreach (var slug in projects)
        {
            if (string.IsNullOrEmpty(slug) || slug.Contains('/', StringComparison.Ordinal))
            {
                throw new ArgumentException("A project slug must be one non-empty path segment.", nameof(projects));
            }
        }

        return projects.ToFrozenSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Why a hit was returned (doc 18 and the retrieval note 3.2). <see cref="Extracted"/> means literal evidence: the
/// terms appear in the text, or a wiki-link points there. <see cref="Inferred"/> means a similarity match only.
/// </summary>
public enum VaultHitBasis
{
    Extracted,
    Inferred,
}

/// <summary>A search request. <see cref="QueryVector"/> is null when no embedding profile is available.</summary>
public sealed record VaultSearchQuery(
    Guid VaultId,
    string Text,
    VaultCaller Caller,
    string? Project = null,
    string? Type = null,
    int Limit = 10);

/// <summary>
/// One card per matching document (doc 18, "Search"; retrieval note 3.4): enough to decide whether to open it.
/// The body is one call away by <see cref="DocId"/> and <see cref="Ordinal"/>.
/// </summary>
public sealed record VaultCard(
    Guid DocId,
    int Ordinal,
    string Path,
    string Title,
    string Type,
    string? Project,
    IReadOnlyList<string> HeadingPath,
    VaultHitBasis Basis,
    double Score,
    string Gist,
    int ApproxTokens);

/// <summary>
/// Search response. <see cref="LexicalOnly"/> is true when no embedding was used, and the response says so.
/// <see cref="IndexedCommit"/> lets callers see how stale the index is (doc 18, "Behaviour").
/// </summary>
public sealed record VaultSearchResult(IReadOnlyList<VaultCard> Cards, bool LexicalOnly, string? IndexedCommit);

/// <summary>Search tuning. Defaults follow doc 18's settings table and the retrieval note.</summary>
public sealed record VaultSearchOptions
{
    /// <summary>The RRF constant (<c>vault.search.rrf_k</c>).</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>Block candidates taken from each list before fusion.</summary>
    public int CandidatePool { get; init; } = 50;

    /// <summary>Maximum characters of body text in a card's gist.</summary>
    public int GistLength { get; init; } = 200;

    /// <summary>Maximum neighbour cards returned by link expansion.</summary>
    public int NeighbourLimit { get; init; } = 8;
}

/// <summary>Produces a query embedding under one profile. The indexer's embedder is wired separately.</summary>
public interface IVaultQueryEmbedder
{
    Guid ProfileId { get; }

    /// <summary>
    /// False when no profile is active right now, so search runs lexical-only and says so. A fixed embedder is always available; a
    /// holder that follows the active profile (host wiring) reports false while none is active.
    /// </summary>
    bool IsAvailable => true;

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}
