using Bazaar.Server.Community.Actions;

namespace Bazaar.Server.Community.Observatory;

/// <summary>
/// One line of the Observatory community log (doc 36, "Rate limits, audit and takedown"; doc 23). It is a projection of
/// <c>community_action</c>. It has no body field: the Observatory shows what was done and where, never what was said.
/// </summary>
/// <param name="Id">Row id of <c>community_action</c>.</param>
/// <param name="AgentId">Agent that took the action.</param>
/// <param name="Platform">Platform key from the policy matrix.</param>
/// <param name="Action">Outward action.</param>
/// <param name="TargetUrl">Target of the action. For a withdrawal this holds the withdrawn item's external id.</param>
/// <param name="BodySha256Hex">Lowercase hex SHA-256 of the body, or null when the action had no body.</param>
/// <param name="Status">Outcome of the action, for example "done".</param>
/// <param name="CreatedAt">Time the row was written.</param>
/// <param name="ApprovalId">Approval that allowed the action, if one was needed.</param>
/// <param name="ExternalId">Platform id of the item created or changed, if known.</param>
public sealed record CommunityLogRow(
    long Id,
    string AgentId,
    string Platform,
    CommunityAction Action,
    string? TargetUrl,
    string? BodySha256Hex,
    string Status,
    DateTimeOffset CreatedAt,
    string? ApprovalId,
    string? ExternalId);
