namespace Bazaar.Server.GraphRetrieval;

/// <summary>A seed node with its fused score.</summary>
public readonly record struct SeedScore(int Node, double Score);

/// <summary>How the subgraph is ranked (doc 64 section 3A.4).</summary>
public enum RankMethod
{
    /// <summary>Parent score times edge weight times decay per hop, best path wins.</summary>
    HopDecay,

    /// <summary>Personalised PageRank over the reached subgraph.</summary>
    Ppr,
}

/// <summary>
/// Bounded multi-hop expansion (doc 64 section 3A.2). Optional values fall back to the engine options.
/// Edge weights of 0 are never followed. A seed keeps its own score; every other node keeps the best score over paths
/// of at most <see cref="MaxHops"/> hops (score = parent score x edge weight x decay).
/// </summary>
public sealed record ExpansionRequest
{
    public required GraphSnapshot Snapshot { get; init; }

    public required IReadOnlyList<SeedScore> Seeds { get; init; }

    /// <summary>Hop budget. 0 returns the seeds only.</summary>
    public int MaxHops { get; init; } = 2;

    /// <summary>Per-hop multiplier in (0, 1].</summary>
    public double? HopDecay { get; init; }

    public EdgeDirection Direction { get; init; } = EdgeDirection.Outgoing;

    /// <summary>Weight multiplier for incoming edges when <see cref="Direction"/> is <see cref="EdgeDirection.Both"/> (backlinks at half weight by default).</summary>
    public double? ReverseWeightFactor { get; init; }

    /// <summary>Kinds followed; null follows all.</summary>
    public EdgeKindSet? Kinds { get; init; }

    /// <summary>Visibility mask; null allows every node.</summary>
    public NodeMask? Mask { get; init; }

    /// <summary>Per-node cap on edges followed, applied in the order weight (descending) then node id.</summary>
    public int? MaxEdgesPerNode { get; init; }

    /// <summary>Per-query cap on reached nodes (seeds included). New nodes are admitted by score, then node id.</summary>
    public int? MaxReached { get; init; }
}

/// <summary>Ancestor or descendant closure along DAG kinds (doc 64 section 3A.3). Edge weights are ignored: closure is structural.</summary>
public sealed record ClosureRequest
{
    public required GraphSnapshot Snapshot { get; init; }

    public required IReadOnlyList<int> Roots { get; init; }

    /// <summary>Outgoing gives ancestors, Incoming gives descendants.</summary>
    public EdgeDirection Direction { get; init; } = EdgeDirection.Outgoing;

    /// <summary>Kinds followed; null uses <see cref="EdgeKindSet.Dag"/>.</summary>
    public EdgeKindSet? Kinds { get; init; }

    public int? MaxDepth { get; init; }

    public int? MaxNodes { get; init; }

    public NodeMask? Mask { get; init; }
}

/// <summary>
/// Personalised PageRank. The restart vector is the normalised seed scores; transitions are proportional to edge weight;
/// the walk stops after <see cref="Iterations"/> or when the L1 change falls below <see cref="Tolerance"/>.
/// </summary>
public sealed record RankRequest
{
    public required GraphSnapshot Snapshot { get; init; }

    public required IReadOnlyList<SeedScore> Seeds { get; init; }

    public double? Alpha { get; init; }

    public int? Iterations { get; init; }

    public double? Tolerance { get; init; }

    public EdgeDirection Direction { get; init; } = EdgeDirection.Outgoing;

    public double? ReverseWeightFactor { get; init; }

    public EdgeKindSet? Kinds { get; init; }

    /// <summary>Visibility mask; masked nodes have no transitions in or out.</summary>
    public NodeMask? Mask { get; init; }

    /// <summary>Optional restriction to a subgraph (for example the nodes reached by expansion); null walks everything reachable from the seeds.</summary>
    public NodeMask? Subgraph { get; init; }

    /// <summary>Optional cap on the returned list (highest first).</summary>
    public int? Limit { get; init; }
}

/// <summary>Expansion, optional DAG closure, ranking and path assembly in one call (doc 64 sections 3A.2 to 3A.5).</summary>
public sealed record SubgraphRequest
{
    public required GraphSnapshot Snapshot { get; init; }

    public required IReadOnlyList<SeedScore> Seeds { get; init; }

    public NodeMask? Mask { get; init; }

    // Expansion
    public int MaxHops { get; init; } = 2;

    public double? HopDecay { get; init; }

    public EdgeDirection Direction { get; init; } = EdgeDirection.Outgoing;

    public double? ReverseWeightFactor { get; init; }

    public EdgeKindSet? Kinds { get; init; }

    public int? MaxEdgesPerNode { get; init; }

    public int? MaxReached { get; init; }

    // Ranking
    public RankMethod Method { get; init; } = RankMethod.HopDecay;

    public double? Alpha { get; init; }

    public int? Iterations { get; init; }

    public double? Tolerance { get; init; }

    // Closure from the seeds; null or empty kinds disables it
    public EdgeKindSet? ClosureKinds { get; init; }

    public EdgeDirection ClosureDirection { get; init; } = EdgeDirection.Outgoing;

    public int? ClosureMaxDepth { get; init; }

    /// <summary>Share of <see cref="NodeCap"/> reserved for closure nodes, 0 to 1.</summary>
    public double? ClosureShare { get; init; }

    /// <summary>Maximum nodes in the result.</summary>
    public int NodeCap { get; init; } = 50;
}
