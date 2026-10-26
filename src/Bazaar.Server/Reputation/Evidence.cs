namespace Bazaar.Server.Reputation;

/// <summary>
/// One scored outcome for an agent and tag. <c>X</c> is in [0,1] (1 = good). <c>ReviewerReliability</c> in [0,1]
/// scales the weight of a peer review and is ignored for other kinds.
/// </summary>
public sealed record Evidence
{
    public Evidence(EvidenceKind kind, double x, DateTimeOffset occurredAt, double reviewerReliability = 1.0)
    {
        if (!(x >= 0 && x <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "Evidence x must be in [0,1].");
        }

        if (!(reviewerReliability >= 0 && reviewerReliability <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(reviewerReliability), reviewerReliability, "Reviewer reliability must be in [0,1].");
        }

        Kind = kind;
        X = x;
        OccurredAt = occurredAt;
        ReviewerReliability = reviewerReliability;
    }

    public EvidenceKind Kind { get; }

    public double X { get; }

    public DateTimeOffset OccurredAt { get; }

    public double ReviewerReliability { get; }
}
