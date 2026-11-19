using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Memory.GitVault;

/// <summary>
/// The Vault search service as the memory lexical index. The search runs as the owner caller because the access layer is
/// applied by the memory tools, not here; hits are cut to the requested area and one per document.
/// </summary>
public sealed class VaultSearchLexicalIndex(VaultSearchService search, Guid vaultId) : IMemoryLexicalIndex
{
    // The search collapses to documents but covers the whole vault, so ask for more than needed before the area cut.
    private const int _overfetch = 4;

    private readonly VaultSearchService _search = search ?? throw new ArgumentNullException(nameof(search));

    /// <inheritdoc />
    public async Task<IReadOnlyList<RankedHit>> SearchAsync(string queryText, string areaPrefix, int maxCandidates, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaPrefix);
        if (string.IsNullOrWhiteSpace(queryText) || maxCandidates <= 0)
        {
            return [];
        }

        var query = new VaultSearchQuery(
            vaultId, queryText, VaultCaller.Owner, Limit: maxCandidates * _overfetch);
        var result = await _search.SearchAsync(query, ct).ConfigureAwait(false);

        return result.Cards
            .Where(card => card.Path.StartsWith(areaPrefix, StringComparison.Ordinal))
            .GroupBy(card => card.Path, StringComparer.Ordinal)
            .Select(group => new RankedHit(group.Key, group.Max(card => card.Score)))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Id, StringComparer.Ordinal)
            .Take(maxCandidates)
            .ToList();
    }
}
