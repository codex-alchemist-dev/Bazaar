namespace Bazaar.Server.Community.Actions;

/// <summary>
/// One row of <c>community_action</c> (doc 36). On append, <see cref="Id"/> is 0 and <see cref="CreatedAt"/> is default;
/// the database sets both. <see cref="Body"/> is per-person content (encryption is a TODO, doc 04).
/// </summary>
/// <param name="Id">Identity value, set by the database.</param>
/// <param name="AgentId">Agent that took the action.</param>
/// <param name="SponsorId">Human sponsor of that agent.</param>
/// <param name="Platform">Platform key from the policy matrix.</param>
/// <param name="Action">Outward action (doc 36, <see cref="CommunityAction"/>).</param>
/// <param name="TargetUrl">Target of the action. For a withdrawal this holds the withdrawn item's external id.</param>
/// <param name="Body">Text posted or sent, if any.</param>
/// <param name="BodySha256">SHA-256 of the UTF-8 body. Computed by the store when left null and a body is given.</param>
/// <param name="ApprovalId">Approval that allowed the action, if one was needed.</param>
/// <param name="TaintSourcesJson">JSON array of taint sources, as text. "[]" when none.</param>
/// <param name="ExternalId">Platform id of the item created or changed, if known.</param>
/// <param name="Status">Outcome of the action, for example "done".</param>
/// <param name="CreatedAt">Set by the database.</param>
public sealed record CommunityActionRecord(
    long Id,
    string AgentId,
    string SponsorId,
    string Platform,
    CommunityAction Action,
    string? TargetUrl,
    string? Body,
    byte[]? BodySha256,
    string? ApprovalId,
    string TaintSourcesJson,
    string? ExternalId,
    string Status,
    DateTimeOffset CreatedAt);
