using System.Text.Json.Serialization;

namespace Bazaar.Server.Council.Membership;

/// <summary>
/// The handoff from the Council to the owning docs (doc 07 for Soul and lifecycle state, doc 25 for hiring).
/// Carries the decision only. Reasoning text never appears here; <see cref="ReasonsSummaryRef"/> points at the
/// <c>council.decision</c> GC event that holds the reasons.
/// </summary>
/// <param name="VoteId">The Council vote.</param>
/// <param name="SubjectAgentId">The subject's agent id.</param>
/// <param name="Kind">The motion kind.</param>
/// <param name="Decision">The final outcome.</param>
/// <param name="Choice">The final choice (yes or no) for approve/reject outcomes; null when withdrawn.</param>
/// <param name="ReasonsSummaryRef">Reference to the reasons summary (no reasoning text).</param>
/// <param name="DecidedAt">When the outcome was decided.</param>
/// <param name="VaultDecisionNo">NNNN of the Vault decision file, when one was written; otherwise null.</param>
public sealed record MembershipOutcome(
    Guid VoteId,
    string SubjectAgentId,
    MembershipMotionKind Kind,
    MembershipDecision Decision,
    string? Choice,
    string ReasonsSummaryRef,
    DateTimeOffset DecidedAt,
    int? VaultDecisionNo)
{
    /// <summary>Current schema version of the handoff. Bump it on any incompatible change.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The schema version this payload was written with.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}
