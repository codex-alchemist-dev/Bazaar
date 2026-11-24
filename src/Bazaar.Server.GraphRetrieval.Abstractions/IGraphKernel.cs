namespace Bazaar.Server.GraphRetrieval;

/// <summary>
/// The graph kernel seam (doc 64 section 4.2): heavy graph inner loops over a <see cref="GraphSnapshot"/>.
/// Expected failures return a <see cref="DeclineReason"/> instead of throwing; the caller then runs its fallback path.
/// The kernel returns node indices, levels and scores only; the caller keeps authority over ids, text and access checks.
/// </summary>
public interface IGraphKernel
{
    KernelCapabilities Capabilities { get; }

    /// <summary>Typed weighted multi-hop expansion from scored seeds, with a hop budget and per-hop decay.</summary>
    ValueTask<KernelResult<ExpansionResult>> TryExpandAsync(ExpansionRequest request, CancellationToken ct);

    /// <summary>Ancestor or descendant closure along DAG edge kinds, cycle-safe.</summary>
    ValueTask<KernelResult<ClosureResult>> TryClosureAsync(ClosureRequest request, CancellationToken ct);

    /// <summary>Personalised PageRank (random walk with restart) from seed scores.</summary>
    ValueTask<KernelResult<NodeScores>> TryRankAsync(RankRequest request, CancellationToken ct);

    /// <summary>Expansion, optional closure and ranking, assembled into ranked nodes plus the paths that justify them.</summary>
    ValueTask<KernelResult<SubgraphResult>> TryAssembleSubgraphAsync(SubgraphRequest request, CancellationToken ct);
}

/// <summary>What a kernel can do and how it behaves.</summary>
/// <param name="Name">Engine name for diagnostics, for example "cpu".</param>
/// <param name="Expand">Supports <see cref="IGraphKernel.TryExpandAsync"/>.</param>
/// <param name="Closure">Supports <see cref="IGraphKernel.TryClosureAsync"/>.</param>
/// <param name="Rank">Supports <see cref="IGraphKernel.TryRankAsync"/>.</param>
/// <param name="Subgraph">Supports <see cref="IGraphKernel.TryAssembleSubgraphAsync"/>.</param>
/// <param name="Deterministic">Results do not depend on thread count or scheduling.</param>
public readonly record struct KernelCapabilities(string Name, bool Expand, bool Closure, bool Rank, bool Subgraph, bool Deterministic);

/// <summary>Why a kernel declined a call. The first values are doc 64 section 4.2; <see cref="InvalidRequest"/> is added for malformed input.</summary>
public enum DeclineReason
{
    None,
    Disabled,
    NoDevice,
    NotResident,
    IndexBehind,
    Deadline,
    DeviceLost,
    BreakerOpen,
    Unsupported,
    InvalidRequest,
}

/// <summary>A kernel answer: a value, or a decline reason with no value.</summary>
public readonly record struct KernelResult<T>(T? Value, DeclineReason Reason, long IndexWatermark)
{
    public bool Succeeded => Reason == DeclineReason.None && Value is not null;
}

/// <summary>Factory helpers for <see cref="KernelResult{T}"/>.</summary>
public static class KernelResult
{
    public static KernelResult<T> Ok<T>(T value, long watermark) => new(value, DeclineReason.None, watermark);

    public static KernelResult<T> Decline<T>(DeclineReason reason, long watermark) => new(default, reason, watermark);
}
