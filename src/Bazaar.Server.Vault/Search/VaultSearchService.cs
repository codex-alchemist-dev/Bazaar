using Bazaar.Server.Vault.Data;

namespace Bazaar.Server.Vault.Search;

/// <summary>
/// Hybrid Vault search (doc 18, "Search"): full-text and vector candidates are fused with RRF, then collapsed to one
/// card per document. Without an embedder the search is lexical only, and the result says so. Link neighbours are
/// expanded before search by context assembly (retrieval note 3.7); the body is fetched by id.
/// </summary>
public sealed class VaultSearchService(
    VaultSearchRepository repository,
    IVaultQueryEmbedder? embedder = null,
    VaultSearchOptions? options = null)
{
    private readonly VaultSearchOptions _options = options ?? new VaultSearchOptions();

    public async Task<VaultSearchResult> SearchAsync(VaultSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            throw new ArgumentException("A search needs query text.", nameof(query));
        }

        if (query.Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "The result limit must be positive.");
        }

        var pool = Math.Max(_options.CandidatePool, query.Limit);
        var lexical = await repository.LexicalCandidatesAsync(
            query.VaultId, query.Text, query.Caller, query.Project, query.Type, pool, cancellationToken).ConfigureAwait(false);

        var vector = Array.Empty<VaultBlockRow>();
        if (embedder is not null)
        {
            var queryVector = await embedder.EmbedAsync(query.Text, cancellationToken).ConfigureAwait(false);
            ValidateVector(queryVector);
            vector = (await repository.VectorCandidatesAsync(
                query.VaultId, embedder.ProfileId, queryVector, query.Caller, query.Project, query.Type, pool, cancellationToken)
                .ConfigureAwait(false)).ToArray();
        }

        var rows = new Dictionary<(Guid DocId, int Ordinal), VaultBlockRow>();
        foreach (var row in lexical.Concat(vector))
        {
            rows.TryAdd((row.DocId, row.Ordinal), row);
        }

        var lexicalKeys = lexical.Select(row => (row.DocId, row.Ordinal)).ToHashSet();
        var fused = ReciprocalRankFusion.Fuse(
            [lexical.Select(row => (row.DocId, row.Ordinal)).ToArray(), vector.Select(row => (row.DocId, row.Ordinal)).ToArray()],
            _options.RrfK);

        // Collapse to documents: the best-fused block of each document stands for it.
        var cards = new List<VaultCard>();
        var documents = new HashSet<Guid>();
        foreach (var entry in fused)
        {
            var row = rows[entry.Key];
            if (!documents.Add(row.DocId))
            {
                continue;
            }

            // A literal text match is Extracted evidence; a similarity-only match is Inferred (retrieval note 3.2).
            var basis = lexicalKeys.Contains(entry.Key) ? VaultHitBasis.Extracted : VaultHitBasis.Inferred;
            cards.Add(ToCard(row, basis, entry.Score));
            if (cards.Count == query.Limit)
            {
                break;
            }
        }

        var indexedCommit = await repository.GetIndexedCommitAsync(query.VaultId, cancellationToken).ConfigureAwait(false);
        return new VaultSearchResult(cards, LexicalOnly: embedder is null, IndexedCommit: indexedCommit);
    }

    /// <summary>
    /// Cards for the documents one wiki-link away from the seeds (depth 1), readable by the caller. Basis is Extracted:
    /// a link is evidence someone wrote down.
    /// </summary>
    public async Task<IReadOnlyList<VaultCard>> ExpandNeighboursAsync(
        Guid vaultId, IReadOnlyCollection<Guid> seedDocIds, VaultCaller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seedDocIds);
        var rows = await repository.NeighbourCardsAsync(
            vaultId, seedDocIds, caller, _options.NeighbourLimit, cancellationToken).ConfigureAwait(false);
        return rows.Select(row => ToCard(row, VaultHitBasis.Extracted, 0)).ToList();
    }

    /// <summary>The body of one block by id, or null when the caller cannot read it or it does not exist.</summary>
    public Task<string?> FetchBodyAsync(Guid vaultId, Guid docId, int ordinal, VaultCaller caller, CancellationToken cancellationToken) =>
        repository.GetBodyAsync(vaultId, docId, ordinal, caller, cancellationToken);

    private VaultCard ToCard(VaultBlockRow row, VaultHitBasis basis, double score) => new(
        row.DocId,
        row.Ordinal,
        row.Path,
        row.Title,
        row.Type,
        row.Project,
        row.HeadingPath,
        basis,
        score,
        Gist(row.Body, _options.GistLength),
        ApproxTokens(row.Body));

    /// <summary>The first <paramref name="length"/> characters with whitespace collapsed, ending in "..." when cut.</summary>
    public static string Gist(string body, int length)
    {
        var collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= length)
        {
            return collapsed;
        }

        return string.Concat(collapsed.AsSpan(0, Math.Max(0, length - 3)), "...");
    }

    /// <summary>A rough token count (about four characters per token), enough for budgeting before a body is opened.</summary>
    public static int ApproxTokens(string body) => (body.Length + 3) / 4;

    private static void ValidateVector(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0 || vector.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("The query embedding must be non-empty and finite.", nameof(vector));
        }
    }
}
