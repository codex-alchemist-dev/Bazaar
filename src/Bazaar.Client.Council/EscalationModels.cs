using System.Text.Json.Serialization;

namespace Bazaar.Client.Council;

/// <summary>One ballot on an escalated vote (empty list while the ballots are still sealed).</summary>
public sealed record EscalationBallot(string VoterId, string Phase, string Choice, string Reasoning, bool Tainted);

/// <summary>One option's count in one round.</summary>
public sealed record EscalationCount(string Choice, int Count);

/// <summary>The packet's dissent summary (docs/design/10, Escalation). Absent when the ballots were never revealed.</summary>
public sealed record EscalationDissent(
    EscalationCount[] Independent,
    EscalationCount[] FollowUp,
    string? LeadingChoice,
    int DissentingBallots,
    int NonAbstainingBallots,
    double? DissentShare);

/// <summary>The packet's taint flags: counts only (doc 15 <c>tainted_at_write</c>).</summary>
public sealed record EscalationTaint(bool AnyTainted, int TaintedBallots, int TotalBallots, string[] TaintedVoters);

/// <summary>One escalated vote awaiting the owner, as <c>GET /council/escalations</c> lists it.</summary>
public sealed record EscalationCard(
    Guid VoteId,
    Guid GcId,
    string Subject,
    string Kind,
    string MotionClass,
    string[] Options,
    int EligibleCount,
    int IndependentBallotCount,
    bool BallotsRevealed,
    EscalationBallot[] Ballots,
    string? EscalationReason,
    DateTimeOffset EscalatedAt,
    DateTimeOffset DeadlineAt,
    long AgeSeconds,
    EscalationDissent? Dissent,
    EscalationTaint Taint);

/// <summary>The listing body.</summary>
public sealed record EscalationListDto(EscalationCard[] Escalations);

/// <summary>The resolve request body.</summary>
public sealed record ResolveEscalationRequest(string Choice, string? Reasoning);

/// <summary>The resolve answer: the vote and the option the owner picked.</summary>
public sealed record ResolveEscalationResult(Guid VoteId, string Choice);

/// <summary>Source-generated JSON for the Council escalation wire types.</summary>
[JsonSerializable(typeof(EscalationListDto))]
[JsonSerializable(typeof(ResolveEscalationRequest))]
[JsonSerializable(typeof(ResolveEscalationResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class CouncilEscalationJsonContext : JsonSerializerContext;

/// <summary>
/// The owner's escalation API (docs/design/10, Escalation). Windows and Android both call this one surface; the UI is not part of it.
/// </summary>
public interface ICouncilEscalationClient
{
    /// <summary>Lists the votes awaiting the owner, oldest escalation first.</summary>
    ValueTask<IReadOnlyList<EscalationCard>> ListEscalationsAsync(CancellationToken cancellationToken);

    /// <summary>Resolves one escalated vote with a listed option and optional reasoning.</summary>
    /// <exception cref="CouncilEscalationException">The server refused: not the owner, no longer escalated, or not a listed option.</exception>
    ValueTask ResolveEscalationAsync(Guid voteId, string choice, string? reasoning, CancellationToken cancellationToken);
}

/// <summary>A refusal from the escalation routes, with the HTTP status and the server's error code.</summary>
public sealed class CouncilEscalationException(int statusCode, string? code) : Exception($"Council escalation request failed with {statusCode} ({code ?? "no code"}).")
{
    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The server's error code (for example <c>not_escalated</c>), when the body had one.</summary>
    public string? Code { get; } = code;
}
