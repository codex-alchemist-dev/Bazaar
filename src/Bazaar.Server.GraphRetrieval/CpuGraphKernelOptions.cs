namespace Bazaar.Server.GraphRetrieval;

/// <summary>
/// Settings of the multi-core CPU engine. Defaults follow doc 64 section 12; where the doc is silent the default is
/// noted in "Implementation notes" of doc 64. A request value, when present, overrides the matching default here.
/// </summary>
public sealed record CpuGraphKernelOptions
{
    /// <summary>Worker threads (doc 64 <c>graph_retrieval.cpu.threads</c>): core count minus 2, at least 1.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 2);

    /// <summary>Frontier or node chunk size per parallel work item. Results never depend on it (chunk results merge in chunk order).</summary>
    public int ChunkSize { get; init; } = 1024;

    /// <summary>Per-hop decay (today's retriever: 0.5).</summary>
    public double HopDecay { get; init; } = 0.5;

    /// <summary>Incoming-edge weight factor when both directions are followed (doc 64 decision 12: backlinks at half weight).</summary>
    public double ReverseWeightFactor { get; init; } = 0.5;

    /// <summary>Per-node edge cap (<c>graph_retrieval.expand.max_frontier</c>).</summary>
    public int MaxEdgesPerNode { get; init; } = 2_000;

    /// <summary>Per-query reached-node cap (seeds included).</summary>
    public int MaxReached { get; init; } = 100_000;

    /// <summary>PageRank restart probability (<c>graph_retrieval.ppr.alpha</c>).</summary>
    public double Alpha { get; init; } = 0.15;

    /// <summary>PageRank iteration limit (<c>graph_retrieval.ppr.iterations</c>).</summary>
    public int Iterations { get; init; } = 20;

    /// <summary>PageRank L1 convergence tolerance.</summary>
    public double Tolerance { get; init; } = 1e-6;

    /// <summary>Closure depth (<c>graph_retrieval.dag.max_depth</c>).</summary>
    public int ClosureMaxDepth { get; init; } = 8;

    /// <summary>Closure node cap.</summary>
    public int ClosureMaxNodes { get; init; } = 10_000;

    /// <summary>Share of a subgraph's node cap reserved for closure nodes (<c>graph_retrieval.dag.budget_share</c>).</summary>
    public double ClosureShare { get; init; } = 0.2;

    /// <summary>Throws <see cref="ArgumentException"/> if a value is outside its safe range.</summary>
    public void Validate()
    {
        Check(MaxDegreeOfParallelism >= 1, nameof(MaxDegreeOfParallelism));
        Check(ChunkSize >= 1, nameof(ChunkSize));
        Check(HopDecay is > 0 and <= 1, nameof(HopDecay));
        Check(ReverseWeightFactor is >= 0 and <= 1, nameof(ReverseWeightFactor));
        Check(MaxEdgesPerNode >= 1, nameof(MaxEdgesPerNode));
        Check(MaxReached >= 1, nameof(MaxReached));
        Check(Alpha is > 0 and < 1, nameof(Alpha));
        Check(Iterations >= 1, nameof(Iterations));
        Check(Tolerance >= 0, nameof(Tolerance));
        Check(ClosureMaxDepth >= 0, nameof(ClosureMaxDepth));
        Check(ClosureMaxNodes >= 1, nameof(ClosureMaxNodes));
        Check(ClosureShare is >= 0 and <= 1, nameof(ClosureShare));
    }

    private static void Check(bool ok, string name)
    {
        if (!ok)
        {
            throw new ArgumentException($"Option {name} is out of range.", name);
        }
    }
}
