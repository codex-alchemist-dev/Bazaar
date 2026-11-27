namespace Bazaar.Server.OutcomeLedger.Abstractions;

/// <summary>Kinds of outcome evidence in the reputation ledger (doc 11, <c>outcome_event.kind</c>).</summary>
public enum OutcomeKind
{
    CiFirstRun,
    Revert,
    PostMergeDefect,
    OwnerAccept,
    OwnerReject,
    Manual,
}

/// <summary>Who an outcome counts for, on one skill tag. <c>W</c> overrides the outcome's weight for this agent when set.</summary>
public sealed record OutcomeAttribution(string AgentId, string Role, string Tag, decimal? W = null);

/// <summary>
/// One piece of outcome evidence. <c>X</c> is in [0,1] (1 = good). <c>W</c> is the weight; leave it null to take the shipped
/// weight for the kind from <see cref="OutcomeWeights"/>. <c>DedupeKey</c> is <c>source:delivery-id</c> for webhooks and
/// <c>source:event-id</c> for polled events (see <see cref="OutcomeDedupeKey"/>), so a replay never counts twice.
/// </summary>
public sealed record OutcomeRecord(
    OutcomeKind Kind,
    string PrRef,
    decimal X,
    string DedupeKey,
    DateTimeOffset OccurredAt,
    IReadOnlyList<OutcomeAttribution> Attributions,
    decimal? W = null,
    string? SourceRef = null);

/// <summary>What <see cref="IOutcomeRecorder.RecordAsync"/> did. <c>Recorded</c> is false when the dedupe key already existed.</summary>
public sealed record OutcomeRecordResult(bool Recorded, Guid? OutcomeId);

/// <summary>Builds dedupe keys in the form doc 11 fixes.</summary>
public static class OutcomeDedupeKey
{
    /// <summary>Returns <c>source:id</c>, for example <c>github:delivery-8f2c</c>.</summary>
    public static string For(string source, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return source + ":" + id;
    }
}

/// <summary>
/// Weight per outcome kind, the shipped defaults of doc 11's evidence table (PROPOSAL, weights OPEN: calibration on fixtures may
/// change them, and the operator can set them).
/// </summary>
public sealed class OutcomeWeights
{
    public decimal OwnerAccept { get; init; } = 1.0m;

    public decimal OwnerReject { get; init; } = 1.0m;

    public decimal Revert { get; init; } = 1.0m;

    public decimal PostMergeDefect { get; init; } = 1.0m;

    public decimal CiFirstRun { get; init; } = 0.5m;

    public decimal Manual { get; init; } = 1.0m;

    /// <summary>The weight for a kind.</summary>
    public decimal For(OutcomeKind kind) => kind switch
    {
        OutcomeKind.OwnerAccept => OwnerAccept,
        OutcomeKind.OwnerReject => OwnerReject,
        OutcomeKind.Revert => Revert,
        OutcomeKind.PostMergeDefect => PostMergeDefect,
        OutcomeKind.CiFirstRun => CiFirstRun,
        OutcomeKind.Manual => Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
