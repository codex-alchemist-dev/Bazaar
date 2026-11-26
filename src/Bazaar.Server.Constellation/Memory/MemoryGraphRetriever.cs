using Bazaar.Server.Constellation.Memory.ReadLog;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Graph-expanded retrieval over memory notes (docs/design/constellation-memory-graph.md, "Retrieval").
/// Seeds are fused with <see cref="RrfFusion"/>, expanded along active <see cref="MemoryEdgeKind.Link"/>,
/// <see cref="MemoryEdgeKind.DerivedFrom"/> and <see cref="MemoryEdgeKind.Supersedes"/> edges, filtered by
/// <see cref="IVaultAccess"/> before they score or expand, and packed under the token budget. Only Active
/// revisions are returned. Expansion never widens the scope a principal can read.
/// Token counts for candidate doc ids ("D" format), asked once per retrieval for the final candidate set. A missing id is not packed.
/// </summary>
public delegate Task<IReadOnlyDictionary<string, int>> MemoryTokenCounter(IReadOnlyCollection<string> docIds, CancellationToken ct);

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
    private const int _maxHopsCeiling = 2;
    private const double _hopPenalty = 0.5;

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
    private const string _deprecatedFrontMatterStatus = "deprecated";
    private const int _maxHopsCeiling = 2;
    private const double _hopPenalty = 0.5;
    private const int _maxSupersedeSteps = 16;

    private readonly IConstellationNodeReader _nodes;
    private readonly IVaultAccess _access;
    private readonly IMemoryVectorSeedSource? _vectorSeeds;
    private readonly IMemoryReadLog? _readLog;

    /// <param name="nodes">Source of the doc nodes, their memory attributes and their edges.</param>
    /// <param name="access">The Vault access layer; every candidate must pass it.</param>
    /// <param name="vectorSeeds">
    /// Vector seeds for query retrieval (<see cref="RetrieveForQueryAsync"/>). Null means no embedding is wired: every query call is
    /// lexical only with reason <see cref="MemoryLexicalOnlyReasons.ProfileNone"/>.
    /// </param>
    /// <param name="readLog">
    /// The owner-read log (docs/design/constellation-memory-graph.md, "Owner-read access log"). Null means no logging. When set, a
    /// retrieval whose reader is not the subject agent of the notes it returns records the read before the result is handed back; if
    /// the record cannot be written the call throws <see cref="MemoryReadLogException"/> and returns nothing (fail closed).
    /// </param>
    public MemoryGraphRetriever(
        IConstellationNodeReader nodes,
        IVaultAccess access,
        IMemoryVectorSeedSource? vectorSeeds = null,
        IMemoryReadLog? readLog = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(access);
        _nodes = nodes;
        _access = access;
        _vectorSeeds = vectorSeeds;
        _readLog = readLog;
    }

    /// <summary>
    /// Retrieval for a query text (docs/design/constellation-memory-graph.md, "Vector seeds"). The query is embedded once and its
    /// vector seeds, scoped to what <paramref name="who"/> may read and to <see cref="MemoryGraphOptions.ScopePrefixes"/> before
    /// ranking, are fused with <paramref name="lexical"/>. No profile, a provider failure or a dimension mismatch degrades to
    /// lexical seeds alone: the result then has <c>LexicalOnly = true</c> and a reason code, and nothing is thrown.
    /// <paramref name="tokenCounts"/> is asked once, for the final candidate set, so vector seeds and hop targets get counts too.
    /// </summary>
    public async Task<MemoryGraphRetrieval> RetrieveForQueryAsync(
        VaultPrincipal who,
        string queryText,
        IReadOnlyList<RankedHit> lexical,
        MemoryTokenCounter tokenCounts,
        int tokenBudget,
        MemoryGraphOptions options,
        int vectorCandidates,
        CancellationToken ct,
        MemoryReadContext? readContext = null)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(tokenCounts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(vectorCandidates, 1);

        MemoryVectorSeeds vector = _vectorSeeds is null
            ? MemoryVectorSeeds.LexicalOnly(MemoryLexicalOnlyReasons.ProfileNone)
            : await _vectorSeeds.GetSeedsAsync(who, queryText, options.ScopePrefixes, vectorCandidates, ct).ConfigureAwait(false);

        MemoryGraphRetrieval result = await RetrieveCoreAsync(who, lexical, vector.Seeds, tokenCounts, tokenBudget, options, readContext, ct)
            .ConfigureAwait(false);
        return vector.Seeds.Count == 0
            ? result with { LexicalOnly = true, LexicalOnlyReason = vector.LexicalOnlyReason ?? MemoryLexicalOnlyReasons.NoVectorsYet }
            : result;
    }

    /// <summary>
    /// Retrieves the packed hits for <paramref name="who"/>. <paramref name="lexical"/> and <paramref name="vector"/> are
    /// ranked lists of note ids (not vault paths). <paramref name="tokenCounts"/> is keyed by note id; a candidate with
    /// no count is skipped rather than failing the call. <see cref="MemoryGraphOptions.MaxHops"/> is clamped to [0, 2].
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
        CancellationToken ct,
        MemoryReadContext? readContext = null)
    {
        ArgumentNullException.ThrowIfNull(tokenCounts);
        return await RetrieveCoreAsync(who, lexical, vector, (_, _) => Task.FromResult(tokenCounts), tokenBudget, options, readContext, ct)
            .ConfigureAwait(false);
    }

    private async Task<MemoryGraphRetrieval> RetrieveCoreAsync(
        VaultPrincipal who,
        IReadOnlyList<RankedHit> lexical,
        IReadOnlyList<RankedHit> vector,
        MemoryTokenCounter countTokens,
        int tokenBudget,
        MemoryGraphOptions options,
        MemoryReadContext? readContext,
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
        IReadOnlyList<string>? scope = options.ScopePrefixes;

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
        // Kept candidates by doc id: the current nodes. Rejected ids were loaded or seen and failed the status, visibility or
        // scope check, so they are never loaded again and never count as a hop target. History holds superseded nodes with the
        // head that replaces them; a superseded node never expands and is never a current candidate.
        var kept = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var history = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var heads = new Dictionary<string, ConstellationGraphNode>(StringComparer.Ordinal);

        IReadOnlyDictionary<string, ConstellationGraphNode> seedNodes = await LoadVisibleAsync(
            who,
            seeds.Select(seed => seed.Id).ToArray(),
            scope,
            ct);

        var frontier = new List<Candidate>();
        foreach (FusedHit seed in seeds)
        {
            string? key = Key(seed.Id);
            if (key is null || kept.ContainsKey(key) || history.ContainsKey(key) || rejected.Contains(key))
            {
                continue;
            }

            if (!seedNodes.TryGetValue(key, out ConstellationGraphNode? node))
            {
                rejected.Add(key);
                continue;
            }

            var candidate = new Candidate(node, seed.Score, HopDistance: 0, seed.FromLexical, seed.FromVector, ViaDocId: null);
            Candidate? admitted = await AdmitAsync(who, candidate, scope, kept, history, heads, ct);
            if (admitted is not null)
            {
                frontier.Add(admitted);
            }
        }

        for (int hop = 1; hop <= maxHops && frontier.Count > 0; hop++)
        {
            // Score every proposed neighbour from this round's frontier. The best path wins.
            var proposed = new Dictionary<string, (double Score, string Via)>(StringComparer.Ordinal);
            foreach (Candidate parent in frontier)
            {
                foreach (ConstellationGraphEdge edge in parent.Node.Edges)
                {
                    if (!PgConstellationNodeReader.IsExpanding(edge.Kind))
                    {
                        continue;
                    }

                    double score = parent.Score * edge.Weight * _hopPenalty;

                    if (kept.TryGetValue(edge.TargetId, out Candidate? existing))
                    string target = edge.TargetDocId.ToString("D");
                    double score = parent.Score * edge.Weight * _hopPenalty;

                    if (kept.TryGetValue(target, out Candidate? existing))
                    {
                        // Seeds keep their fused score. An expansion hit found by a higher-scoring path keeps the higher score,
                        // and the hop distance stays the smaller one it already has.
                        if (existing.HopDistance > 0 && score > existing.Score)
                        {
                            kept[edge.TargetId] = existing with { Score = score };
                            kept[target] = existing with { Score = score };
                        }

                        continue;
                    }

                    if (rejected.Contains(edge.TargetId))
                    if (rejected.Contains(target) || history.ContainsKey(target))
                    {
                        continue;
                    }

                    proposed[edge.TargetId] = proposed.TryGetValue(edge.TargetId, out double best)
                        ? Math.Max(best, score)
                        : score;
                    if (!proposed.TryGetValue(target, out var best) || score > best.Score)
                    {
                        proposed[target] = (score, parent.Id);
                    }
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
            IReadOnlyDictionary<string, ConstellationGraphNode> loaded = await LoadVisibleAsync(
                who,
                proposed.Keys.ToArray(),
                scope,
                ct);

            var nextFrontier = new List<Candidate>();
            foreach (var pair in proposed.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (!loaded.TryGetValue(pair.Key, out ConstellationGraphNode? node))
                {
                    rejected.Add(pair.Key);
                    continue;
                }

                var candidate = new Candidate(revision, pair.Value, hop, FromLexical: false, FromVector: false);
                kept[pair.Key] = candidate;
                nextFrontier.Add(candidate);
                if (kept.ContainsKey(pair.Key) || history.ContainsKey(pair.Key))
                {
                    continue;
                }

                var candidate = new Candidate(node, pair.Value.Score, hop, FromLexical: false, FromVector: false, pair.Value.Via);
                Candidate? admitted = await AdmitAsync(who, candidate, scope, kept, history, heads, ct);
                if (admitted is not null)
                {
                    nextFrontier.Add(admitted);
                }
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
        // A history entry is never ranked above its head: its score is capped at the head's final score, and on a tie the head
        // sorts first. History is returned only when asked for.
        var ordered = kept.Values.ToList();
        if (options.IncludeHistory)
        {
            foreach (Candidate old in history.Values)
            {
                if (kept.TryGetValue(old.HeadDocId!, out Candidate? head))
                {
                    ordered.Add(old with { Score = Math.Min(old.Score, head.Score) });
                }
            }
        }

        ordered = ordered
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.HeadDocId is null ? 0 : 1)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .ToList();

        // Candidates without a token count cannot be packed. Skip them instead of failing the call.
        IReadOnlyDictionary<string, int> tokenCounts = await countTokens(
            [.. ordered.Select(candidate => candidate.Id)],
            ct).ConfigureAwait(false);
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

        var byId = ordered.ToDictionary(candidate => candidate.NoteId, StringComparer.Ordinal);
        var byId = ordered.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
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
        await RecordReadAsync(who, hits, readContext, ct).ConfigureAwait(false);
        return new MemoryGraphRetrieval(hits, lexicalOnly);
    }

    /// <summary>
    /// Writes the owner-read log entries for a result, one per subject agent whose private area (or, for a null subject, a non-agent
    /// area) was read by a principal other than that agent. An agent reading its own area, or an agent reading a non-memory doc, is not
    /// logged here (the retrieval audit entry covers it). Nothing is written for an empty result: nothing was read.
    /// </summary>
    private async Task RecordReadAsync(VaultPrincipal who, List<MemoryGraphHit> hits, MemoryReadContext? context, CancellationToken ct)
    {
        if (_readLog is null || hits.Count == 0)
        {
            return;
        }

        // A cover agent never reads the primary's private memory (doc 08 section 8), so its own id is its own area.
        string? ownAgent = who.Kind is VaultPrincipalKind.Agent or VaultPrincipalKind.Cover ? who.AgentId : null;
        bool isAgent = who.Kind is VaultPrincipalKind.Agent or VaultPrincipalKind.Cover;

        var entries = new List<MemoryReadEntry>();
        foreach (var group in hits.GroupBy(hit => hit.ScopeAgentId, StringComparer.Ordinal))
        {
            // An agent's reads of its own area, and of docs outside any agent's area, go to the audit log only.
            if (isAgent && (group.Key is null || string.Equals(group.Key, ownAgent, StringComparison.Ordinal)))
            {
                continue;
            }

            entries.Add(new MemoryReadEntry(
                context?.ReaderPrincipal ?? DefaultReader(who),
                context?.Session,
                context?.Route ?? MemoryReadContext.DefaultRoute,
                group.Key,
                MemoryReadOperations.Retrieve,
                [.. group.Where(hit => hit.NoteId is not null).Select(hit => hit.NoteId!)],
                [.. group.Select(hit => hit.DocId)],
                context?.TaintTags ?? [],
                context?.Purpose));
        }

        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            await _readLog.RecordAsync(entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MemoryReadLogException)
        {
            throw new MemoryReadLogException("The memory read could not be logged.", ex);
        }
    }

    private static string DefaultReader(VaultPrincipal who) => who.Kind switch
    {
        VaultPrincipalKind.Owner => "owner",
        VaultPrincipalKind.Maintenance => "maintenance",
        VaultPrincipalKind.MemberHuman => "member:" + who.MemberId,
        VaultPrincipalKind.Cover => "agent:" + who.AgentId,
        _ => "agent:" + who.AgentId,
    };

    /// <summary>
    /// Admits a loaded, visible candidate. A node with no readable current superseder is kept as current and returned for
    /// expansion. A superseded node is recorded as history under the head of its chain; the head is kept in its place with at least
    /// the superseded node's score (so the head of a supersedes chain ranks first) and is returned for expansion when it is new.
    /// </summary>
    private async Task<Candidate?> AdmitAsync(
        VaultPrincipal who,
        Candidate candidate,
        IReadOnlyList<string>? scope,
        Dictionary<string, Candidate> kept,
        Dictionary<string, Candidate> history,
        Dictionary<string, ConstellationGraphNode> heads,
        CancellationToken ct)
    {
        ConstellationGraphNode head = await ResolveHeadAsync(who, candidate.Node, scope, heads, ct);
        string headId = head.DocId.ToString("D");
        if (headId == candidate.Id)
        {
            kept[candidate.Id] = candidate;
            return candidate;
        }

        history[candidate.Id] = candidate with { HeadDocId = headId };
        if (kept.TryGetValue(headId, out Candidate? existing))
        {
            if (candidate.Score > existing.Score)
            {
                kept[headId] = existing with { Score = candidate.Score };
            }

            return null;
        }

        history.Remove(headId);
        var replacement = new Candidate(head, candidate.Score, candidate.HopDistance, candidate.FromLexical, candidate.FromVector, candidate.Id);
        kept[headId] = replacement;
        return replacement;
    }

    /// <summary>
    /// Follows incoming supersedes edges to the current head: the first node reached that no readable, active, in-scope node
    /// supersedes. Superseders the caller cannot see do not count, so a hidden note never changes what the caller is shown. The walk
    /// is bounded and cycle-safe; among several readable superseders the lowest doc id is taken, so the result is deterministic.
    /// </summary>
    private async Task<ConstellationGraphNode> ResolveHeadAsync(
        VaultPrincipal who,
        ConstellationGraphNode node,
        IReadOnlyList<string>? scope,
        Dictionary<string, ConstellationGraphNode> heads,
        CancellationToken ct)
    {
        string start = node.DocId.ToString("D");
        if (heads.TryGetValue(start, out ConstellationGraphNode? cached))
        {
            return cached;
        }

        var seen = new HashSet<Guid> { node.DocId };
        ConstellationGraphNode current = node;
        for (int step = 0; step < _maxSupersedeSteps; step++)
        {
            Guid[] next = [.. (current.SupersededBy ?? []).Where(id => !seen.Contains(id)).Order()];
            if (next.Length == 0)
            {
                break;
            }

            IReadOnlyDictionary<string, ConstellationGraphNode> visible = await LoadVisibleAsync(
                who,
                [.. next.Select(id => id.ToString("D"))],
                scope,
                ct);
            ConstellationGraphNode? successor = next
                .Select(id => visible.TryGetValue(id.ToString("D"), out ConstellationGraphNode? found) ? found : null)
                .FirstOrDefault(found => found is not null);
            if (successor is null)
            {
                break;
            }

            seen.Add(successor.DocId);
            current = successor;
        }

        heads[start] = current;
        return current;
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
        IReadOnlyList<string>? scope,
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

        var requested = new HashSet<string>(noteIds, StringComparer.Ordinal);
        IReadOnlyList<MemoryGraphRevision> revisions = await _store.GetActiveAsync(noteIds, ct);

        foreach (MemoryGraphRevision revision in revisions)
        {
            if (!requested.Contains(revision.NoteId)
                || !string.Equals(revision.Status, _activeStatus, StringComparison.Ordinal))
        IReadOnlyList<ConstellationGraphNode> nodes = await _nodes.GetNodesAsync(requested, ct);

        foreach (ConstellationGraphNode node in nodes)
        {
            if (!requested.Contains(node.DocId)
                || string.Equals(node.FrontMatterStatus, _deprecatedFrontMatterStatus, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!VaultPath.TryParse(revision.VaultPath, out VaultPath? path) || !_access.CanRead(who, path))
            if (node.Memory is { } memory && !string.Equals(memory.Status, _activeStatus, StringComparison.Ordinal))
            {
                continue;
            }

            visible[revision.NoteId] = revision;
            // The doc path decides, and a memory note must also be readable at the path its revision names, so a stale or
            // mismatched index row can never open another agent's note.
            if (!CanRead(who, node.VaultPath) || (node.Memory is { } note && !CanRead(who, note.VaultPath)))
            {
                continue;
            }

            if (scope is not null && !InScope(scope, node.VaultPath))
            {
                continue;
            }

            visible[node.DocId.ToString("D")] = node;
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
    private static bool InScope(IReadOnlyList<string> scope, string path) =>
        scope.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

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
            candidate.HopDistance,
            IsHistory: candidate.HeadDocId is not null,
            SupersededBy: candidate.HeadDocId,
            Version: memory?.Version,
            ViaDocId: candidate.ViaDocId);
    }

    /// <param name="HeadDocId">Set for a history entry: the head of its supersedes chain.</param>
    private sealed record Candidate(
        ConstellationGraphNode Node,
        double Score,
        int HopDistance,
        bool FromLexical,
        bool FromVector,
        string? ViaDocId,
        string? HeadDocId = null)
    {
        public string Id => Node.DocId.ToString("D");
    }
}
