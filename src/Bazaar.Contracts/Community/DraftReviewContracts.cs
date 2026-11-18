using System.Text.Json.Serialization;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Contracts.Community;

/// <summary>One quality-gate result shown on a draft-review card (doc 36, "Quality gates before any Tier 2 post").</summary>
/// <param name="Gate">Gate name, as the server's gate check reports it.</param>
/// <param name="Passed">True when the gate passed.</param>
/// <param name="Detail">Short reason when the gate failed; empty when it passed.</param>
public sealed record QualityGateFindingDto(string Gate, bool Passed, string Detail);

/// <summary>
/// The draft-review card a client renders for one outbound community post (doc 36, work package 8). The exact body is shown
/// with its hash, and the embedded approval card carries the signature binding, so what the owner reads is what is posted.
/// </summary>
/// <param name="DraftId">Draft id; also the idempotency scope for the post.</param>
/// <param name="Platform">Platform key, for example <c>github</c>.</param>
/// <param name="Kind">Item kind: <c>issue</c>, <c>pr</c>, <c>thread</c> or <c>comment</c>.</param>
/// <param name="Target">Target URL or <c>owner/repo</c> reference.</param>
/// <param name="Title">Title for an issue or PR; null for a comment.</param>
/// <param name="Body">Exact outbound text, disclosure footer included. Shown quoted, never rendered as markup that can act.</param>
/// <param name="BodySha256">Lowercase hex SHA-256 of <paramref name="Body"/>. The approval is void if it changes.</param>
/// <param name="FooterPresent">True when the AI-disclosure footer is in the body. A false value disables approval in the UI.</param>
/// <param name="Gates">Quality-gate results. Any failed gate disables approval in the UI.</param>
/// <param name="Approval">The approval card this draft is bound to (doc 15).</param>
public sealed record DraftReviewCardDto(
    Guid DraftId,
    string Platform,
    string Kind,
    string Target,
    string? Title,
    string Body,
    string BodySha256,
    bool FooterPresent,
    IReadOnlyList<QualityGateFindingDto> Gates,
    ApprovalCardDto Approval);

/// <summary>
/// The owner's reply to a draft-review card. An edited body is never posted under the old approval: the server treats
/// <paramref name="EditedBody"/> as a new draft that needs a new approval (doc 36, behaviour and edge cases).
/// </summary>
/// <param name="DraftId">Draft being decided.</param>
/// <param name="Decision">The signed approval decision (doc 15).</param>
/// <param name="EditedBody">Replacement body when the decision is edit-then-approve; otherwise null.</param>
public sealed record DraftReviewDecisionDto(Guid DraftId, ApprovalDecisionDto Decision, string? EditedBody);

/// <summary>Source-generated JSON metadata for the community draft-review contracts. Options match the approval contracts.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(QualityGateFindingDto))]
[JsonSerializable(typeof(DraftReviewCardDto))]
[JsonSerializable(typeof(DraftReviewDecisionDto))]
public sealed partial class CommunityContractsJsonContext : JsonSerializerContext;
