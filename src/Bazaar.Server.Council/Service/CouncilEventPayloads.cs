using System.Text.Json.Serialization;

namespace Bazaar.Server.Council.Service;

/// <summary>
/// Source-generated JSON for the council GC event payloads. Property names are snake_case and the properties are declared
/// in the order the events have always written them, so the emitted bytes match the old reflection-based output exactly.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CouncilOpenPayload))]
[JsonSerializable(typeof(CouncilTransitionPayload))]
[JsonSerializable(typeof(CouncilRevealPayload))]
[JsonSerializable(typeof(CouncilDecisionPayload))]
[JsonSerializable(typeof(CouncilReasonPayload))]
internal sealed partial class CouncilEventJsonContext : JsonSerializerContext;

/// <summary>The <c>council.open</c> payload (no reasoning).</summary>
internal sealed record CouncilOpenPayload
{
    public required Guid VoteId { get; init; }

    public required Guid GcId { get; init; }

    public required string SubjectRef { get; init; }

    public required string Kind { get; init; }

    public required string MotionClass { get; init; }

    /// <summary>Set by the proposer when the vote opens (doc 10, Follow-up on dissent).</summary>
    public bool HighImpact { get; init; }

    public required IReadOnlyList<string> Options { get; init; }

    public required string ProposerId { get; init; }

    public required string? SubjectAuthorId { get; init; }

    public required IReadOnlyList<string> EligibleVoterIds { get; init; }

    public required DateTimeOffset DeadlineAt { get; init; }

    public required long RevealSeed { get; init; }
}

/// <summary>The <c>council.transition</c> payload, one per written version.</summary>
internal sealed record CouncilTransitionPayload
{
    public required Guid VoteId { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    public required string Reason { get; init; }

    public required long Version { get; init; }

    public required DateTimeOffset DeadlineAt { get; init; }

    public required int Extensions { get; init; }
}

/// <summary>The <c>council.reveal</c> payload: the tally and the reasons in reason order.</summary>
internal sealed record CouncilRevealPayload
{
    public required Guid VoteId { get; init; }

    public required Guid GcId { get; init; }

    public required long RevealSeed { get; init; }

    public required IReadOnlyList<string> ReasonOrder { get; init; }

    public required CouncilRevealTallyPayload Tally { get; init; }

    public required IReadOnlyList<CouncilRevealReasonPayload> Reasons { get; init; }
}

/// <summary>The tally inside <c>council.reveal</c>.</summary>
internal sealed record CouncilRevealTallyPayload
{
    public required string Outcome { get; init; }

    public required string? Choice { get; init; }

    public required string? EscalationReason { get; init; }

    public required IReadOnlyDictionary<string, int> Counts { get; init; }
}

/// <summary>One reason inside <c>council.reveal</c>.</summary>
internal sealed record CouncilRevealReasonPayload
{
    public required string VoterId { get; init; }

    public required string Choice { get; init; }
}

/// <summary>
/// The clear part of a <c>council.reason</c> event (doc 10, Envelope). The reasoning text is not here: it is sealed in the
/// event's <c>payload_ct</c> under the voter's key.
/// </summary>
internal sealed record CouncilReasonPayload
{
    public required Guid VoteId { get; init; }

    public required string VoterId { get; init; }

    public required string Phase { get; init; }

    public required string Choice { get; init; }

    /// <summary><c>reveal</c> for independent reasons and <c>decision</c> for follow-up reasons.</summary>
    public required string Context { get; init; }

    public required bool ErasedAtWrite { get; init; }
}

/// <summary>The <c>council.decision</c> payload.</summary>
internal sealed record CouncilDecisionPayload
{
    public required Guid VoteId { get; init; }

    public required Guid GcId { get; init; }

    public required string DecidedBy { get; init; }

    public required string Choice { get; init; }

    public required string? OwnerReasoning { get; init; }

    public required int? VaultDecisionNo { get; init; }

    public required IReadOnlyDictionary<string, int> FinalCounts { get; init; }

    public required IReadOnlyList<CouncilDecisionVoterPayload> Voters { get; init; }
}

/// <summary>One voter inside <c>council.decision</c>.</summary>
internal sealed record CouncilDecisionVoterPayload
{
    public required string VoterId { get; init; }

    public required string? IndependentChoice { get; init; }

    public required string? FollowupChoice { get; init; }

    public required string? FinalChoice { get; init; }
}
