using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Graph-expanded retrieval over memory notes (docs/design/constellation-memory-graph.md, "Retrieval").
/// Seeds are fused with <see cref="RrfFusion"/>, expanded along active <see cref="MemoryEdgeKind.Link"/>,
/// <see cref="MemoryEdgeKind.DerivedFrom"/> and <see cref="MemoryEdgeKind.Supersedes"/> edges, filtered by
/// <see cref="IVaultAccess"/> before they score or expand, and packed under the token budget. Only Active
/// revisions are returned. Expansion never widens the scope a principal can read.
/// </summary>
public sealed class MemoryGraphRetriever
{
    private const string ActiveStatus = "Active";
    private const int MaxHopsCeiling = 2;
    private const double HopPenalty = 0.5;

    private readonly IMemoryGraphStore _store;
    private readonly IVaultAccess _access;

    /// <param name="store">Source of the active revisions.</param>
    /// <param name="access">The Vault access layer; every candidate must pass it.</param>
    public MemoryGraphRetriever(IMemoryGraphStore store, IVaultAccess access)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        _store = store;
        _access = access;
    }

    /// <summary>
    /// Retrieves the packed hits for <paramref name="who"/>. <paramref name="lexical"/> and <paramref name="vector"/> are
    /// ranked lists of note ids (not vault paths). <paramref name="tokenCounts"/> is keyed by note id; a candidate with
    /// no count is skipped rather than failing the call. <see cref="MemoryGraphOptions.MaxHops"/> is clamped to [0, 2].
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

        int maxHops = Math.Clamp(options.MaxHops, 0, MaxHopsCeiling);
        bool lexicalOnly = vector.Count == 0;

        IReadOnlyList<FusedHit> seeds = RrfFusion.Fuse(
            lexical,
            vector,
            new RetrievalOptions(TokenBudget: tokenBudget, TopK: options.TopK, RrfK: options.RrfK));

        // Kept candidates by note id. Rejected ids were loaded or seen and failed the status or visibility check,
        // so they are never loaded again and never count as a hop target.
        var kept = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyDictionary<string, MemoryGraphRevision> seedRevisions = await LoadVisibleAsync(
            who,
            seeds.Select(seed => seed.Id).ToArray(),
            ct);

        foreach (FusedHit seed in seeds)
        {
            if (seedRevisions.TryGetValue(seed.Id, out MemoryGraphRevision? revision))
            {
                kept[seed.Id] = new Candidate(revision, seed.Score, HopDistance: 0, seed.FromLexical, seed.FromVector);
            }
            else
            {
                rejected.Add(seed.Id);
            }
        }

        List<Candidate> frontier = kept.Values.ToList();
        for (int hop = 1; hop <= maxHops && frontier.Count > 0; hop++)
        {
            // Score every proposed neighbour from this round's frontier. The best path wins.
            var proposed = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Candidate parent in frontier)
            {
                foreach (MemoryGraphEdge edge in parent.Revision.Edges)
                {
                    if (!IsExpanding(edge.Kind))
                    {
                        continue;
                    }

                    double score = parent.Score * edge.Weight * HopPenalty;

                    if (kept.TryGetValue(edge.TargetId, out Candidate? existing))
                    {
                        // Seeds keep their fused score. An expansion hit found by a higher-scoring path keeps the higher score,
                        // and the hop distance stays the smaller one it already has.
                        if (existing.HopDistance > 0 && score > existing.Score)
                        {
                            kept[edge.TargetId] = existing with { Score = score };
                        }

                        continue;
                    }

                    if (rejected.Contains(edge.TargetId))
                    {
                        continue;
                    }

                    proposed[edge.TargetId] = proposed.TryGetValue(edge.TargetId, out double best)
                        ? Math.Max(best, score)
                        : score;
                }
            }

            if (proposed.Count == 0)
            {
                break;
            }

            IReadOnlyDictionary<string, MemoryGraphRevision> loaded = await LoadVisibleAsync(
                who,
                proposed.Keys.ToArray(),
                ct);

            var nextFrontier = new List<Candidate>();
            foreach (KeyValuePair<string, double> pair in proposed.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (!loaded.TryGetValue(pair.Key, out MemoryGraphRevision? revision))
                {
                    rejected.Add(pair.Key);
                    continue;
                }

                var candidate = new Candidate(revision, pair.Value, hop, FromLexical: false, FromVector: false);
                kept[pair.Key] = candidate;
                nextFrontier.Add(candidate);
            }

            frontier = nextFrontier;
        }

        var ordered = kept.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NoteId, StringComparer.Ordinal)
            .ToList();

        // Candidates without a token count cannot be packed. Skip them instead of failing the call.
        var packable = ordered
            .Where(candidate => tokenCounts.ContainsKey(candidate.NoteId))
            .Select(candidate => new FusedHit(
                candidate.NoteId,
                candidate.Score,
                candidate.FromLexical,
                candidate.FromVector,
                lexicalOnly))
            .ToList();

        IReadOnlyList<FusedHit> packed = TokenBudgetPacker.Pack(packable, tokenCounts, tokenBudget);

        var byId = ordered.ToDictionary(candidate => candidate.NoteId, StringComparer.Ordinal);
        var hits = packed
            .Select(hit => ToHit(byId[hit.Id]))
            .ToList();

        return new MemoryGraphRetrieval(hits, lexicalOnly);
    }

    private static bool IsExpanding(MemoryEdgeKind kind) =>
        kind is MemoryEdgeKind.Link or MemoryEdgeKind.DerivedFrom or MemoryEdgeKind.Supersedes;

    /// <summary>
    /// Loads the active revisions of <paramref name="noteIds"/> in one store call and keeps only those the principal may
    /// read. Revisions that are not Active or whose vault path is invalid or hidden are left out.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MemoryGraphRevision>> LoadVisibleAsync(
        VaultPrincipal who,
        string[] noteIds,
        CancellationToken ct)
    {
        var visible = new Dictionary<string, MemoryGraphRevision>(StringComparer.Ordinal);
        if (noteIds.Length == 0)
        {
            return visible;
        }

        var requested = new HashSet<string>(noteIds, StringComparer.Ordinal);
        IReadOnlyList<MemoryGraphRevision> revisions = await _store.GetActiveAsync(noteIds, ct);

        foreach (MemoryGraphRevision revision in revisions)
        {
            if (!requested.Contains(revision.NoteId)
                || !string.Equals(revision.Status, ActiveStatus, StringComparison.Ordinal))
            {
                continue;
            }

            if (!VaultPath.TryParse(revision.VaultPath, out VaultPath? path) || !_access.CanRead(who, path))
            {
                continue;
            }

            visible[revision.NoteId] = revision;
        }

        return visible;
    }

    private static MemoryGraphHit ToHit(Candidate candidate)
    {
        MemoryGraphRevision revision = candidate.Revision;
        return new MemoryGraphHit(
            revision.NoteId,
            revision.VaultPath,
            revision.Title,
            revision.ScopeAgentId,
            revision.Kind,
            revision.Trust,
            candidate.Score,
            candidate.HopDistance);
    }

    private sealed record Candidate(
        MemoryGraphRevision Revision,
        double Score,
        int HopDistance,
        bool FromLexical,
        bool FromVector)
    {
        public string NoteId => Revision.NoteId;
    }
}
