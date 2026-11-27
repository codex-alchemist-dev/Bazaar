namespace Bazaar.Server.ReviewLedger.Abstractions;

/// <summary>
/// One peer review of a PR (doc 11, <c>review</c> table). <c>Overall</c> is 1 to 5. <c>RubricJson</c> is the rubric as a JSON
/// object. <c>RationaleRef</c> is the Vault path and commit of the rationale note (PROPOSED). <c>SupersedesId</c> replaces an
/// earlier review by the same reviewer on the same PR; the old row stays.
/// </summary>
public sealed record ReviewRecord(
    string PrRef,
    string ReviewerAgentId,
    string AuthorAgentId,
    string Tag,
    int Overall,
    bool Blinded,
    Guid? CardId = null,
    string? ReviewerFamily = null,
    string? AuthorFamily = null,
    string? RubricJson = null,
    string? RationaleRef = null,
    Guid? SupersedesId = null);

/// <summary>Why a review was not recorded.</summary>
public enum ReviewRefusal
{
    None,
    SelfReview,
    TooManyReviewers,
    SupersedesUnknown,
    SupersedesMismatch,
}

/// <summary>The outcome of <see cref="IReviewRecorder.RecordAsync"/>.</summary>
public sealed record ReviewRecordResult(Guid? ReviewId, ReviewRefusal Refusal)
{
    public bool Recorded => ReviewId is not null;
}

/// <summary>Settings for review capture (doc 11, PROPOSED defaults).</summary>
public sealed class ReviewCaptureSettings
{
    /// <summary>Peer reviews that count per PR, per distinct reviewer (doc 11: max 2).</summary>
    public int MaxReviewersPerPr { get; init; } = 2;
}
