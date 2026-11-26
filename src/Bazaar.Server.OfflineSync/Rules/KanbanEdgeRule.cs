using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>A dependency edge: <see cref="From"/> must finish before <see cref="To"/>.</summary>
/// <param name="From">The task the edge leaves.</param>
/// <param name="To">The task the edge enters.</param>
public readonly record struct KanbanEdge(string From, string To);

/// <summary>State the Kanban edge rule needs: write-id claims and the edge set per graph with per-edge versions.</summary>
public interface IKanbanEdgeState
{
    /// <summary>Atomically claims a write id. Returns false when the id was already claimed.</summary>
    ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken);

    /// <summary>The edges currently present in a graph.</summary>
    ValueTask<IReadOnlyCollection<KanbanEdge>> GetEdgesAsync(string graphId, CancellationToken cancellationToken);

    /// <summary>The version of one edge; 0 when the edge was never added or removed.</summary>
    ValueTask<long> GetEdgeVersionAsync(string graphId, KanbanEdge edge, CancellationToken cancellationToken);

    /// <summary>Stores whether an edge is present and its new version.</summary>
    ValueTask SetEdgeAsync(string graphId, KanbanEdge edge, bool present, long version, CancellationToken cancellationToken);
}

/// <summary>
/// Merge rule for Kanban dependency edges (doc 56 section 5, item 3). Adds merge as a union. An add
/// that would create a cycle in the result is rejected: the edge that closes the cycle is the one
/// refused. Removes are field changes and need the base version of the edge, like Kanban fields.
/// </summary>
/// <remarks>
/// Order limit: the final edge set is independent of arrival order when the union of all adds is
/// acyclic. When several adds together would form a cycle, the first to arrive wins and the later
/// one is rejected, so arrival order decides which edge is refused. Graph writes for one target
/// must be serialized by the host for the cycle check to see every earlier edge.
/// </remarks>
public sealed class KanbanEdgeRule : IOfflineMergeRule
{
    private readonly IKanbanEdgeState _state;
    private readonly IReviewSink _review;

    /// <summary>Creates the rule.</summary>
    /// <param name="state">The edge state.</param>
    /// <param name="review">Receives conflict review items for removes.</param>
    public KanbanEdgeRule(IKanbanEdgeState state, IReviewSink review)
    {
        _state = state;
        _review = review;
    }

    /// <inheritdoc />
    public OfflineDataClass DataClass => OfflineDataClass.KanbanEdge;

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        var payload = RulePayloadCodec.ReadKanbanEdge(write.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.FromTaskId) || string.IsNullOrWhiteSpace(payload.ToTaskId))
        {
            return RuleOutcomes.Rejected(write.WriteId, "Payload is not a Kanban edge.");
        }

        if (!await _state.TryClaimWriteAsync(write.WriteId, cancellationToken))
        {
            return RuleOutcomes.Duplicate(write.WriteId);
        }

        var edge = new KanbanEdge(payload.FromTaskId, payload.ToTaskId);
        var current = await _state.GetEdgeVersionAsync(write.Target, edge, cancellationToken);
        return payload.Remove
            ? await RemoveAsync(write, edge, current, cancellationToken)
            : await AddAsync(write, edge, current, cancellationToken);
    }

    private async ValueTask<OfflineWriteOutcome> AddAsync(OfflineWrite write, KanbanEdge edge, long current, CancellationToken cancellationToken)
    {
        var edges = await _state.GetEdgesAsync(write.Target, cancellationToken);
        if (edges.Contains(edge))
        {
            return RuleOutcomes.Applied(write.WriteId, RuleVersions.Format(current));
        }

        if (HasCycle([.. edges, edge]))
        {
            return RuleOutcomes.Rejected(write.WriteId, "Edge would create a dependency cycle.");
        }

        await _state.SetEdgeAsync(write.Target, edge, true, current + 1, cancellationToken);
        return RuleOutcomes.Applied(write.WriteId, RuleVersions.Format(current + 1));
    }

    private async ValueTask<OfflineWriteOutcome> RemoveAsync(OfflineWrite write, KanbanEdge edge, long current, CancellationToken cancellationToken)
    {
        var baseVersion = RuleVersions.Parse(write.BaseVersion);
        if (baseVersion is null)
        {
            return RuleOutcomes.Rejected(write.WriteId, "Edge removes must name a numeric base version.");
        }

        if (baseVersion.Value != current)
        {
            var reason = "Edge changed since the base version.";
            await _review.OpenAsync(new ReviewItem("kanban-edge-conflict", write.Target, write.WriteId, write.BaseVersion, RuleVersions.Format(current), reason), cancellationToken);
            return new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.Conflict, RuleVersions.Format(current), reason);
        }

        var edges = await _state.GetEdgesAsync(write.Target, cancellationToken);
        if (!edges.Contains(edge))
        {
            return RuleOutcomes.Applied(write.WriteId, RuleVersions.Format(current));
        }

        await _state.SetEdgeAsync(write.Target, edge, false, current + 1, cancellationToken);
        return RuleOutcomes.Applied(write.WriteId, RuleVersions.Format(current + 1));
    }

    /// <summary>Depth-first cycle check. A self edge counts as a cycle.</summary>
    private static bool HasCycle(IEnumerable<KanbanEdge> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!adjacency.TryGetValue(edge.From, out var targets))
            {
                targets = [];
                adjacency[edge.From] = targets;
            }

            targets.Add(edge.To);
            if (!adjacency.ContainsKey(edge.To))
            {
                adjacency[edge.To] = [];
            }
        }

        // 1 = on the current path, 2 = fully explored.
        var marks = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var node in adjacency.Keys)
        {
            if (!marks.ContainsKey(node) && Visit(node))
            {
                return true;
            }
        }

        return false;

        bool Visit(string node)
        {
            marks[node] = 1;
            foreach (var next in adjacency[node])
            {
                if (marks.TryGetValue(next, out var mark))
                {
                    if (mark == 1)
                    {
                        return true;
                    }

                    continue;
                }

                if (Visit(next))
                {
                    return true;
                }
            }

            marks[node] = 2;
            return false;
        }
    }
}

/// <summary>In-memory <see cref="IKanbanEdgeState"/> for tests and single-process use. Thread safe.</summary>
public sealed class InMemoryKanbanEdgeState : IKanbanEdgeState
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _claimed = [];
    private readonly HashSet<(string GraphId, KanbanEdge Edge)> _present = [];
    private readonly Dictionary<(string GraphId, KanbanEdge Edge), long> _versions = [];

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_claimed.Add(writeId));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<KanbanEdge>> GetEdgesAsync(string graphId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyCollection<KanbanEdge> edges = _present.Where(p => p.GraphId == graphId).Select(p => p.Edge).ToList();
            return ValueTask.FromResult(edges);
        }
    }

    /// <inheritdoc />
    public ValueTask<long> GetEdgeVersionAsync(string graphId, KanbanEdge edge, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_versions.TryGetValue((graphId, edge), out var version) ? version : 0L);
        }
    }

    /// <inheritdoc />
    public ValueTask SetEdgeAsync(string graphId, KanbanEdge edge, bool present, long version, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (present)
            {
                _present.Add((graphId, edge));
            }
            else
            {
                _present.Remove((graphId, edge));
            }

            _versions[(graphId, edge)] = version;
            return ValueTask.CompletedTask;
        }
    }
}
