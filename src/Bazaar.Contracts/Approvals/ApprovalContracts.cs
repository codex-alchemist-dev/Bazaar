using System.Text.Json.Serialization;
using Bazaar.Contracts.Gc;

namespace Bazaar.Contracts.Approvals;

/// <summary>Where an approval stands, as the card shows it (doc 15, approval lifecycle).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalCardStatus>))]
public enum ApprovalCardStatus
{
    /// <summary>Waiting for the owner. Nothing runs.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The owner approved this exact call. It runs once.</summary>
    [JsonStringEnumMemberName("approved")]
    Approved,

    /// <summary>The owner refused. Terminal.</summary>
    [JsonStringEnumMemberName("denied")]
    Denied,

    /// <summary>No decision arrived before expiry. Terminal.</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,

    /// <summary>The approved call ran. Terminal.</summary>
    [JsonStringEnumMemberName("consumed")]
    Consumed,
}

/// <summary>The owner's choice on a card (doc 15, approval flow step 3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalDecisionKind>))]
public enum ApprovalDecisionKind
{
    /// <summary>Approve this exact call.</summary>
    [JsonStringEnumMemberName("approveOnce")]
    ApproveOnce,

    /// <summary>Refuse this call.</summary>
    [JsonStringEnumMemberName("deny")]
    Deny,

    /// <summary>Edit the arguments, then approve the edited call as a new request.</summary>
    [JsonStringEnumMemberName("editThenApprove")]
    EditThenApprove,

    /// <summary>Approve a scoped grant for the rest of this task. The scope is shown, never blanket.</summary>
    [JsonStringEnumMemberName("approveForTask")]
    ApproveForTask,
}

/// <summary>
/// A piece of untrusted content that raised the session's taint, shown on the card. <see cref="Quoted"/> is always
/// true: the text is data to read, never something the card presents as an instruction or as the owner's words.
/// </summary>
/// <param name="SourceId">Id of the event, tool result or message that carried the taint.</param>
/// <param name="Trust">Trust tag the server assigned at ingestion.</param>
/// <param name="Text">Excerpt of the content, clipped by the server, or null when the text is not available.</param>
/// <param name="Quoted">Always true for taint excerpts. Clients render the text inside a quote block.</param>
public sealed record QuotedExcerptDto(string SourceId, SourceTrust Trust, string? Text, bool Quoted);

/// <summary>
/// The approval card a client renders (doc 15, approval flow step 2). Everything the owner approves is here: the exact
/// payload and the hash it is bound to, so the signature covers what the card shows.
/// </summary>
/// <param name="ApprovalId">Approval id. Also the id in the signed message.</param>
/// <param name="AgentId">Agent that asked.</param>
/// <param name="Tool">Tool the approval covers.</param>
/// <param name="Target">Target of the action, if any.</param>
/// <param name="Summary">One human-readable line describing the call.</param>
/// <param name="PayloadText">The exact arguments, as JSON text.</param>
/// <param name="PayloadHash">Lowercase hex of the dispatch hash the approval is bound to.</param>
/// <param name="QuotedExcerpts">Taint lineage, each entry marked as quoted.</param>
/// <param name="Cost">Cost placeholder for the card. Null until a cost estimate exists.</param>
/// <param name="ExpiresAt">The approval is void at or after this time.</param>
/// <param name="Status">Current status.</param>
/// <param name="AvailableDecisions">Decisions the owner may choose now. Empty once the request is decided or expired.</param>
public sealed record ApprovalCardDto(
    Guid ApprovalId,
    string AgentId,
    string Tool,
    string? Target,
    string Summary,
    string PayloadText,
    string PayloadHash,
    IReadOnlyList<QuotedExcerptDto> QuotedExcerpts,
    string? Cost,
    DateTimeOffset ExpiresAt,
    ApprovalCardStatus Status,
    IReadOnlyList<ApprovalDecisionKind> AvailableDecisions);

/// <summary>
/// The owner's decision, sent by a client. The signature is the device's ECDSA P-256 signature (IEEE P1363, 64 bytes)
/// over the decision message; it is base64 text on the wire. <see cref="DeviceId"/> names the signing key.
/// </summary>
/// <param name="ApprovalId">Approval being decided.</param>
/// <param name="Decision">The owner's choice.</param>
/// <param name="DeviceId">Device key id that signed the decision.</param>
/// <param name="SignatureBase64">Base64 signature. Null or missing means no decision.</param>
/// <param name="DecidedAt">When the device made the choice, as the client reports it. The server uses its own clock.</param>
public sealed record ApprovalDecisionDto(
    Guid ApprovalId,
    ApprovalDecisionKind Decision,
    string DeviceId,
    string? SignatureBase64,
    DateTimeOffset DecidedAt);
