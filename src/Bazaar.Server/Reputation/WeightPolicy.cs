namespace Bazaar.Server.Reputation;

/// <summary>
/// Scoring weights and prior. Shipped defaults come from the reputation settings table in doc 11.
/// <c>Version</c> is the <c>weights_version</c> recorded with every snapshot.
/// </summary>
public sealed record WeightPolicy
{
    public static WeightPolicy Default { get; } = new();

    /// <summary>Weight for owner accept, owner reject and manual evidence.</summary>
    public double OwnerOutcome { get; init; } = 1.0;

    public double Revert { get; init; } = 1.0;

    public double PostMergeDefect { get; init; } = 1.0;

    public double CiFirstRun { get; init; } = 0.5;

    /// <summary>Base weight of a peer review, before multiplying by reviewer reliability.</summary>
    public double PeerReview { get; init; } = 0.2;

    /// <summary>Prior weight k, centred at 0.5.</summary>
    public double PriorK { get; init; } = 3.0;

    public double HalfLifeDays { get; init; } = 180.0;

    public int Version { get; init; } = 1;
}
