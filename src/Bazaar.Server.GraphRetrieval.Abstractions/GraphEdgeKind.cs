namespace Bazaar.Server.GraphRetrieval;

/// <summary>The closed set of typed edges that enter the CSR (doc 64 section 5.3). Values are bit positions of <see cref="EdgeKindSet"/>.</summary>
public enum GraphEdgeKind : byte
{
    Link = 0,
    Embed = 1,
    DerivedFrom = 2,
    Supersedes = 3,
    Contradicts = 4,
    ConditionedOn = 5,
    FollowsPrecedent = 6,
    ResolvedBy = 7,
    Affects = 8,
    ApprovedBy = 9,
}

/// <summary>The starting edge weights of doc 64 section 5.3. Hosts may pass their own weight per edge instead.</summary>
public static class GraphEdgeWeights
{
    /// <summary>The starting weight of a kind. <see cref="GraphEdgeKind.Contradicts"/> and unknown kinds are 0: never expanded.</summary>
    public static double Default(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Link => 1.0,
        GraphEdgeKind.Embed => 1.0,
        GraphEdgeKind.DerivedFrom => 0.8,
        GraphEdgeKind.Supersedes => 0.5,
        GraphEdgeKind.ConditionedOn => 0.8,
        GraphEdgeKind.FollowsPrecedent => 0.8,
        GraphEdgeKind.ResolvedBy => 0.6,
        GraphEdgeKind.Affects => 0.6,
        GraphEdgeKind.ApprovedBy => 0.3,
        _ => 0.0,
    };
}

/// <summary>Which way edges are followed. For closure, <see cref="Outgoing"/> yields ancestors (what a node is built on) and <see cref="Incoming"/> descendants.</summary>
public enum EdgeDirection
{
    Outgoing,
    Incoming,
    Both,
}

/// <summary>A set of edge kinds, as a bit mask over <see cref="GraphEdgeKind"/> values (kinds 0 to 31).</summary>
public readonly record struct EdgeKindSet
{
    private readonly uint _bits;

    private EdgeKindSet(uint bits) => _bits = bits;

    /// <summary>Every kind.</summary>
    public static EdgeKindSet All => new(uint.MaxValue);

    /// <summary>No kind.</summary>
    public static EdgeKindSet None => default;

    /// <summary>The kinds doc 64 section 3A.3 treats as DAG kinds.</summary>
    public static EdgeKindSet Dag { get; } = Of(
        GraphEdgeKind.DerivedFrom,
        GraphEdgeKind.Supersedes,
        GraphEdgeKind.ConditionedOn,
        GraphEdgeKind.FollowsPrecedent,
        GraphEdgeKind.ResolvedBy);

    public bool IsEmpty => _bits == 0;

    public static EdgeKindSet Of(params GraphEdgeKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        uint bits = 0;
        foreach (GraphEdgeKind k in kinds)
        {
            if ((byte)k >= 32)
            {
                throw new ArgumentOutOfRangeException(nameof(kinds), "Edge kinds must be below 32.");
            }

            bits |= 1u << (byte)k;
        }

        return new EdgeKindSet(bits);
    }

    public bool Contains(byte kind) => kind < 32 && (_bits & (1u << kind)) != 0;

    public bool Contains(GraphEdgeKind kind) => Contains((byte)kind);
}
