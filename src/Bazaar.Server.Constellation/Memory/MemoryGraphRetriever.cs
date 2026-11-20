using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Graph-expanded retrieval over Constellation's own nodes, the Vault docs (docs/design/constellation-memory-graph.md,
/// "Retrieval" and "Convergence design"). Seeds are doc ids fused with <see cref="RrfFusion"/>, expanded along resolved Vault
/// links and the <see cref="MemoryEdgeKind.DerivedFrom"/> and <see cref="MemoryEdgeKind.Supersedes"/> edges of memory notes,
/// filtered by <see cref="IVaultAccess"/> before they score or expand, and packed under the token budget. A memory note is
/// returned only while its active revision is Active; a doc whose front matter status is deprecated is never returned.
/// Expansion never widens the scope a principal can read.
/// </summary>
public sealed class MemoryGraphRetriever
{
    private const string _activeStatus = "Active";
    private const string _deprecatedFrontMatterStatus = "deprecated";
    private const int _maxHopsCeiling = 2;
    private const double _hopPenalty = 0.5;

    private readonly IConstellationNodeReader _nodes;
    private readonly IVaultAccess _access;

    /// <param name="nodes">Source of the doc nodes, their memory attributes and their edges.</param>
    /// <param name="access">The Vault access layer; every candidate must pass it.</param>
    public MemoryGraphRetriever(IConstellationNodeReader nodes, IVaultAccess access)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(access);
        _nodes = nodes;
        _access = access;
    }

    /// <summary>
    /// Retrieves the packed hits for <paramref name="who"/>. <paramref name="lexical"/> and <paramref name="vector"/> are
    /// ranked lists of Vault doc ids (<c>vault_docs.doc_id</c>, "D" format), not note ids or vault paths; an id that is not a
    /// doc id is dropped. <paramref name="tokenCounts"/> is keyed by the same doc id string; a candidate with no count is
    /// skipped rather than failing the call. <see cref="MemoryGraphOptions.MaxHops"/> is clamped to [0, 2].
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A count, the budget or an RRF or top-k setting is out of range.</exception>
    public async Task<MemoryGraphRetrieval> RetrieveAsync(
        VaultPrincipal who,
        IReadOnlyList<RankedHit> lexical,
        IReadOnlyList<RankedHit> vector,
        IReadOnlyDictionary<string, int> tokenCounts,
        int tokenBudget,
        MemoryGraphOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(tokenCounts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(tokenBudget);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TopK, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RrfK, 1);

        int maxHops = Math.Clamp(options.MaxHops, 0, _maxHopsCeiling);
        bool lexicalOnly = vector.Count == 0;

        IReadOnlyList<FusedHit> seeds = RrfFusion.Fuse(
            lexical,
            vector,
            new RetrievalOptions(TokenBudget: tokenBudget, TopK: options.TopK, RrfK: options.RrfK));

        // Kept candidates by doc id. Rejected ids were loaded or seen and failed the status or visibility check,
        // so they are never loaded again and never count as a hop target.
        var kept = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyDictionary<string, ConstellationGraphNode> seedNodes = await LoadVisibleAsync(
            who,
            seeds.Select(seed => seed.Id).ToArray(),
            ct);

        foreach (FusedHit seed in seeds)
        {
            string? key = Key(seed.Id);
            if (key is not null && seedNodes.TryGetValue(key, out ConstellationGraphNode? node) && !kept.ContainsKey(key))
            {
                kept[key] = new Candidate(node, seed.Score, HopDistance: 0, seed.FromLexical, seed.FromVector);
            }
            else if (key is not null && !kept.ContainsKey(key))
            {
                rejected.Add(key);
            }
        }

        List<Candidate> frontier = kept.Values.ToList();
        for (int hop = 1; hop <= maxHops && frontier.Count > 0; hop++)
        {
            // Score every proposed neighbour from this round's frontier. The best path wins.
            var proposed = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Candidate parent in frontier)
            {
                foreach (ConstellationGraphEdge edge in parent.Node.Edges)
                {
                    if (!PgConstellationNodeReader.IsExpanding(edge.Kind))
                    {
                        continue;
                    }

                    string target = edge.TargetDocId.ToString("D");
                    double score = parent.Score * edge.Weight * _hopPenalty;

                    if (kept.TryGetValue(target, out Candidate? existing))
                    {
                        // Seeds keep their fused score. An expansion hit found by a higher-scoring path keeps the higher score,
                        // and the hop distance stays the smaller one it already has.
                        if (existing.HopDistance > 0 && score > existing.Score)
                        {
                            kept[target] = existing with { Score = score };
                        }

                        continue;
                    }

                    if (rejected.Contains(target))
                    {
                        continue;
                    }

                    proposed[target] = proposed.TryGetValue(target, out double best)
                        ? Math.Max(best, score)
                        : score;
                }
            }

            if (proposed.Count == 0)
            {
                break;
            }

            IReadOnlyDictionary<string, ConstellationGraphNode> loaded = await LoadVisibleAsync(
                who,
                proposed.Keys.ToArray(),
                ct);

            var nextFrontier = new List<Candidate>();
            foreach (KeyValuePair<string, double> pair in proposed.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (!loaded.TryGetValue(pair.Key, out ConstellationGraphNode? node))
                {
                    rejected.Add(pair.Key);
                    continue;
                }

                var candidate = new Candidate(node, pair.Value, hop, FromLexical: false, FromVector: false);
                kept[pair.Key] = candidate;
                nextFrontier.Add(candidate);
            }

            frontier = nextFrontier;
        }

        var ordered = kept.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();

        // Candidates without a token count cannot be packed. Skip them instead of failing the call.
        var packable = ordered
            .Where(candidate => tokenCounts.ContainsKey(candidate.Id))
            .Select(candidate => new FusedHit(
                candidate.Id,
                candidate.Score,
                candidate.FromLexical,
                candidate.FromVector,
                lexicalOnly))
            .ToList();

        IReadOnlyList<FusedHit> packed = TokenBudgetPacker.Pack(packable, tokenCounts, tokenBudget);

        var byId = ordered.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var hits = packed
            .Select(hit => ToHit(byId[hit.Id]))
            .ToList();

        return new MemoryGraphRetrieval(hits, lexicalOnly);
    }

    /// <summary>The canonical doc id string ("D" format) of an id, or null when it is not a doc id.</summary>
    private static string? Key(string id) => Guid.TryParse(id, out Guid docId) ? docId.ToString("D") : null;

    /// <summary>
    /// Loads the nodes of <paramref name="ids"/> in one reader call and keeps only those the principal may read. Ids that are
    /// not doc ids, memory notes whose active revision is not Active, deprecated docs, and nodes whose doc path (or memory
    /// revision path) is invalid or hidden are left out.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ConstellationGraphNode>> LoadVisibleAsync(
        VaultPrincipal who,
        string[] ids,
        CancellationToken ct)
    {
        var visible = new Dictionary<string, ConstellationGraphNode>(StringComparer.Ordinal);
        var requested = new HashSet<Guid>();
        foreach (string id in ids)
        {
            if (Guid.TryParse(id, out Guid docId))
            {
                requested.Add(docId);
            }
        }

        if (requested.Count == 0)
        {
            return visible;
        }

        IReadOnlyList<ConstellationGraphNode> nodes = await _nodes.GetNodesAsync(requested, ct);

        foreach (ConstellationGraphNode node in nodes)
        {
            if (!requested.Contains(node.DocId)
                || string.Equals(node.FrontMatterStatus, _deprecatedFrontMatterStatus, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (node.Memory is { } memory && !string.Equals(memory.Status, _activeStatus, StringComparison.Ordinal))
            {
                continue;
            }

            // The doc path decides, and a memory note must also be readable at the path its revision names, so a stale or
            // mismatched index row can never open another agent's note.
            if (!CanRead(who, node.VaultPath) || (node.Memory is { } note && !CanRead(who, note.VaultPath)))
            {
                continue;
            }

            visible[node.DocId.ToString("D")] = node;
        }

        return visible;
    }

    private bool CanRead(VaultPrincipal who, string path) =>
        VaultPath.TryParse(path, out VaultPath? parsed) && _access.CanRead(who, parsed);

    private static MemoryGraphHit ToHit(Candidate candidate)
    {
        ConstellationGraphNode node = candidate.Node;
        MemoryGraphRevision? memory = node.Memory;
        return new MemoryGraphHit(
            candidate.Id,
            memory?.NoteId,
            node.VaultPath,
            node.Title,
            memory?.ScopeAgentId,
            memory?.Kind ?? node.DocType,
            memory?.Trust,
            candidate.Score,
            candidate.HopDistance);
    }

    private sealed record Candidate(
        ConstellationGraphNode Node,
        double Score,
        int HopDistance,
        bool FromLexical,
        bool FromVector)
    {
        public string Id => Node.DocId.ToString("D");
    }
}
