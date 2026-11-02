namespace Bazaar.Server.Vault.Search;

/// <summary>
/// Who is asking. The owner reads every document; an agent reads shared areas and its own private memory
/// (<see cref="AgentOwner"/>, "agent:&lt;id&gt;"). Other agents' private memory is never returned (doc 18, "Access").
/// </summary>
public sealed record VaultCaller(bool IsOwner, string? AgentOwner)
{
    public static VaultCaller Owner { get; } = new(IsOwner: true, AgentOwner: null);
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

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}
