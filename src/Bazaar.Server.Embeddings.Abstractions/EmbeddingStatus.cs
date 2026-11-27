namespace Bazaar.Server.Embeddings;

/// <summary>
/// How much of a profile's embeddable Vault blocks have a current embedding. Personal-data blocks, and blocks a model refused as too long,
/// are not embeddable and are in neither count.
/// </summary>
public sealed record EmbeddingCoverage(Guid ProfileId, long Embedded, long Embeddable)
{
    /// <summary>Blocks still without a current embedding.</summary>
    public long Backlog => Math.Max(0, Embeddable - Embedded);

    /// <summary>Embedded divided by embeddable; 1 when nothing is embeddable.</summary>
    public decimal Ratio => Embeddable <= 0 ? 1m : (decimal)Embedded / Embeddable;

    /// <summary>True when the ratio is at least <paramref name="threshold"/>. Exact: no rounding at 1.0.</summary>
    public bool Reaches(decimal threshold) => Embedded >= threshold * Embeddable;
}

/// <summary>What one sweep did for one profile. Counts only; no block text.</summary>
public sealed record EmbeddingSweepStatus(
    Guid ProfileId,
    DateTimeOffset CompletedAt,
    int Embedded,
    int SkippedPersonal,
    int TooLong,
    int RetryLater,
    string? Error);

/// <summary>The indexer's state after its latest sweep (doc 08, "Indexer observability" [PROPOSED]).</summary>
/// <param name="Idle">True when no profile is active (setting <c>none</c>), so nothing is indexed.</param>
/// <param name="Active">Coverage of the profile search uses.</param>
/// <param name="Target">Coverage of the profile being backfilled for a planned switch; null when no switch is pending.</param>
/// <param name="SwitchThreshold">Coverage the target must reach before it is activated.</param>
/// <param name="Backlog">Blocks still to embed under the profile being filled: the target when switching, else the active one.</param>
public sealed record EmbeddingStatusSnapshot(
    bool Idle,
    EmbeddingCoverage? Active,
    EmbeddingCoverage? Target,
    decimal SwitchThreshold,
    long Backlog,
    EmbeddingSweepStatus? ActiveSweep,
    EmbeddingSweepStatus? TargetSweep,
    DateTimeOffset? LastActivatedAt)
{
    /// <summary>Before the first sweep.</summary>
    public static EmbeddingStatusSnapshot Initial { get; } = new(true, null, null, 1m, 0, null, null, null);
}

/// <summary>Read side for the dashboard. Reading costs no query: the snapshot is refreshed by each sweep.</summary>
public interface IEmbeddingStatus
{
    Task<EmbeddingStatusSnapshot> GetAsync(CancellationToken cancellationToken);
}

/// <summary>The current value of <c>memory.embedding_switch_threshold</c>. The host adapts its setting store onto this.</summary>
public interface IEmbeddingSwitchSettingSource
{
    Task<decimal> GetSwitchThresholdAsync(CancellationToken cancellationToken);
}
