namespace Bazaar.Server.GraphRetrieval;

/// <summary>
/// The multi-core CPU <see cref="IGraphKernel"/> (doc 64 sections 4.2 and 6): typed weighted expansion, DAG closure,
/// personalised PageRank and subgraph assembly over an immutable <see cref="GraphSnapshot"/>. Results are
/// deterministic and independent of the thread count: parallel work is split into fixed chunks whose results are merged
/// in chunk order under total orders (score, then node id). It is the reference that GPU providers must match.
/// </summary>
public sealed class CpuGraphKernel : IGraphKernel
{
    private readonly CpuGraphKernelOptions _options;

    public CpuGraphKernel()
        : this(new CpuGraphKernelOptions())
    {
    }

    public CpuGraphKernel(CpuGraphKernelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public KernelCapabilities Capabilities { get; } = new("cpu", Expand: true, Closure: true, Rank: true, Subgraph: true, Deterministic: true);

    public ValueTask<KernelResult<ExpansionResult>> TryExpandAsync(ExpansionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GraphSnapshot g = request.Snapshot;
        if (!SeedNormalizer.MaskMatches(g, request.Mask)
            || request.MaxHops < 0
            || !SeedNormalizer.TryNormalize(g, request.Seeds, request.Mask, out SeedScore[] seeds)
            || !TryResolveDecay(request.HopDecay, out double decay)
            || !TryResolveFactor(request.ReverseWeightFactor, out double inFactor)
            || !TryPositive(request.MaxEdgesPerNode, _options.MaxEdgesPerNode, out int perNode)
            || !TryPositive(request.MaxReached, _options.MaxReached, out int maxReached))
        {
            return Invalid<ExpansionResult>(g);
        }

        ExpansionResult result = ExpansionEngine.Run(
            g, seeds, request.MaxHops, decay, request.Direction, inFactor, request.Kinds ?? EdgeKindSet.All, request.Mask, perNode, maxReached, _options, ct);
        return Ok(result, g);
    }

    public ValueTask<KernelResult<ClosureResult>> TryClosureAsync(ClosureRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GraphSnapshot g = request.Snapshot;
        int maxDepth = request.MaxDepth ?? _options.ClosureMaxDepth;
        if (!SeedNormalizer.MaskMatches(g, request.Mask)
            || maxDepth < 0
            || !TryPositive(request.MaxNodes, _options.ClosureMaxNodes, out int maxNodes)
            || !TryRoots(g, request.Roots, request.Mask, out int[] roots))
        {
            return Invalid<ClosureResult>(g);
        }

        ClosureResult result = ClosureEngine.Run(
            g, roots, request.Direction, request.Kinds ?? EdgeKindSet.Dag, maxDepth, maxNodes, request.Mask, _options, ct);
        return Ok(result, g);
    }

    public ValueTask<KernelResult<NodeScores>> TryRankAsync(RankRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GraphSnapshot g = request.Snapshot;
        double alpha = request.Alpha ?? _options.Alpha;
        int iterations = request.Iterations ?? _options.Iterations;
        double tolerance = request.Tolerance ?? _options.Tolerance;
        if (!SeedNormalizer.MaskMatches(g, request.Mask)
            || !SeedNormalizer.MaskMatches(g, request.Subgraph)
            || alpha is not (> 0 and < 1)
            || iterations < 1
            || tolerance < 0
            || request.Limit is < 0
            || !TryResolveFactor(request.ReverseWeightFactor, out double inFactor)
            || !SeedNormalizer.TryNormalize(g, request.Seeds, Combine(request.Mask, request.Subgraph), out SeedScore[] seeds))
        {
            return Invalid<NodeScores>(g);
        }

        NodeScores result = PageRankEngine.Run(
            g, seeds, alpha, iterations, tolerance, request.Direction, inFactor, request.Kinds ?? EdgeKindSet.All, Combine(request.Mask, request.Subgraph), request.Limit, _options, ct);
        return Ok(result, g);
    }

    public ValueTask<KernelResult<SubgraphResult>> TryAssembleSubgraphAsync(SubgraphRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GraphSnapshot g = request.Snapshot;
        double share = request.ClosureShare ?? _options.ClosureShare;
        double alpha = request.Alpha ?? _options.Alpha;
        int iterations = request.Iterations ?? _options.Iterations;
        double tolerance = request.Tolerance ?? _options.Tolerance;
        int closureDepth = request.ClosureMaxDepth ?? _options.ClosureMaxDepth;
        if (!SeedNormalizer.MaskMatches(g, request.Mask)
            || request.MaxHops < 0
            || request.NodeCap < 0
            || share is not (>= 0 and <= 1)
            || alpha is not (> 0 and < 1)
            || iterations < 1
            || tolerance < 0
            || closureDepth < 0
            || !SeedNormalizer.TryNormalize(g, request.Seeds, request.Mask, out SeedScore[] seeds)
            || !TryResolveDecay(request.HopDecay, out double decay)
            || !TryResolveFactor(request.ReverseWeightFactor, out double inFactor)
            || !TryPositive(request.MaxEdgesPerNode, _options.MaxEdgesPerNode, out int perNode)
            || !TryPositive(request.MaxReached, _options.MaxReached, out int maxReached))
        {
            return Invalid<SubgraphResult>(g);
        }

        EdgeKindSet kinds = request.Kinds ?? EdgeKindSet.All;
        ExpansionResult expansion = ExpansionEngine.Run(
            g, seeds, request.MaxHops, decay, request.Direction, inFactor, kinds, request.Mask, perNode, maxReached, _options, ct);

        ClosureResult? closure = null;
        if (request.ClosureKinds is { IsEmpty: false } ck && seeds.Length > 0)
        {
            closure = ClosureEngine.Run(
                g, [.. seeds.Select(static s => s.Node)], request.ClosureDirection, ck, closureDepth, _options.ClosureMaxNodes, request.Mask, _options, ct);
        }

        // Reached set: expansion plus closure.
        var reachedSet = new SortedSet<int>(expansion.Nodes.Select(static r => r.Node));
        if (closure is not null)
        {
            reachedSet.UnionWith(closure.Nodes.Select(static c => c.Node));
        }

        var score = new Dictionary<int, double>();
        int pprIterations = 0;
        bool pprConverged = true;
        if (request.Method == RankMethod.Ppr)
        {
            NodeMask subgraph = NodeMask.FromNodes(g.NodeCount, reachedSet);
            NodeMask effective = request.Mask is null ? subgraph : request.Mask.And(subgraph);
            NodeScores ppr = PageRankEngine.Run(
                g, seeds, alpha, iterations, tolerance, request.Direction, inFactor, kinds, effective, null, _options, ct);
            pprIterations = ppr.Iterations;
            pprConverged = ppr.Converged;
            foreach (NodeScore s in ppr.Scores)
            {
                score[s.Node] = s.Score;
            }
        }
        else
        {
            foreach (ReachedNode r in expansion.Nodes)
            {
                score[r.Node] = r.Score;
            }
        }

        var seedSet = seeds.Select(static s => s.Node).ToHashSet();
        var closureOf = closure?.Nodes.ToDictionary(static c => c.Node) ?? [];
        NodeOrigin OriginOf(int node)
        {
            NodeOrigin o = NodeOrigin.None;
            if (seedSet.Contains(node))
            {
                o |= NodeOrigin.Seed;
            }

            if (expansion.TryGet(node, out ReachedNode rn) && rn.Level > 0)
            {
                o |= NodeOrigin.Expansion;
            }

            if (closureOf.TryGetValue(node, out ClosureNode cn) && cn.Depth > 0)
            {
                o |= NodeOrigin.Closure;
            }

            return o;
        }

        double ScoreOf(int node) => score.TryGetValue(node, out double s) ? s : 0.0;

        // Selection: a reserved share for closure nodes (shallowest first), then the best-scoring rest.
        int cap = request.NodeCap;
        var chosen = new HashSet<int>();
        if (closure is not null && share > 0)
        {
            int quota = (int)Math.Floor(cap * share);
            foreach (ClosureNode c in closure.Nodes.Where(static c => c.Depth > 0)
                .OrderBy(static c => c.Depth)
                .ThenByDescending(c => ScoreOf(c.Node))
                .ThenBy(static c => c.Node))
            {
                if (chosen.Count >= quota)
                {
                    break;
                }

                chosen.Add(c.Node);
            }
        }

        foreach (int node in reachedSet.OrderByDescending(ScoreOf).ThenBy(static n => n))
        {
            if (chosen.Count >= cap)
            {
                break;
            }

            chosen.Add(node);
        }

        int[] ranked = [.. chosen.OrderByDescending(ScoreOf).ThenBy(static n => n)];
        var nodes = new SubgraphNode[ranked.Length];
        var paths = new List<SubgraphPath>();
        for (int i = 0; i < ranked.Length; i++)
        {
            int v = ranked[i];
            int level = expansion.TryGet(v, out ReachedNode r) ? r.Level : (closureOf.TryGetValue(v, out ClosureNode cn) ? cn.Depth : 0);
            nodes[i] = new SubgraphNode(v, ScoreOf(v), i, OriginOf(v), level);
        }

        foreach (int v in ranked.Order())
        {
            if (seedSet.Contains(v))
            {
                continue;
            }

            GraphPath? path = expansion.TracePath(v);
            if (path is null && closureOf.ContainsKey(v))
            {
                path = TraceClosure(closureOf, v);
            }

            if (path is not null)
            {
                paths.Add(new SubgraphPath(v, path));
            }
        }

        var result = new SubgraphResult(nodes, paths, closure?.CycleNodes ?? [], request.Method, pprIterations, pprConverged);
        return Ok(result, g);
    }

    private static GraphPath TraceClosure(Dictionary<int, ClosureNode> closure, int node)
    {
        var nodes = new List<int> { node };
        var kinds = new List<GraphEdgeKind>();
        ClosureNode cur = closure[node];
        while (cur.Predecessor >= 0)
        {
            kinds.Add(cur.PredecessorKind);
            nodes.Add(cur.Predecessor);
            cur = closure[cur.Predecessor];
        }

        nodes.Reverse();
        kinds.Reverse();
        return new GraphPath(nodes, kinds);
    }

    private static NodeMask? Combine(NodeMask? a, NodeMask? b) => a is null ? b : (b is null ? a : a.And(b));

    private bool TryResolveDecay(double? value, out double decay)
    {
        decay = value ?? _options.HopDecay;
        return decay is > 0 and <= 1;
    }

    private bool TryResolveFactor(double? value, out double factor)
    {
        factor = value ?? _options.ReverseWeightFactor;
        return factor is >= 0 and <= 1;
    }

    private static bool TryPositive(int? value, int fallback, out int resolved)
    {
        resolved = value ?? fallback;
        return resolved >= 1;
    }

    private static bool TryRoots(GraphSnapshot g, IReadOnlyList<int> roots, NodeMask? mask, out int[] result)
    {
        result = [];
        var set = new SortedSet<int>();
        foreach (int r in roots)
        {
            if ((uint)r >= (uint)g.NodeCount)
            {
                return false;
            }

            if (mask is null || mask.Contains(r))
            {
                set.Add(r);
            }
        }

        result = [.. set];
        return true;
    }

    private static ValueTask<KernelResult<T>> Ok<T>(T value, GraphSnapshot g) =>
        ValueTask.FromResult(KernelResult.Ok(value, g.Watermark));

    private static ValueTask<KernelResult<T>> Invalid<T>(GraphSnapshot g) =>
        ValueTask.FromResult(KernelResult.Decline<T>(DeclineReason.InvalidRequest, g.Watermark));
}
