namespace Bazaar.Contracts.Community;

/// <summary>
/// One line of the community log (doc 36, "Rate limits, audit and takedown"; doc 23). It mirrors the server's log row and
/// has no body field on purpose: the log shows what was done and where, never what was said.
/// </summary>
/// <param name="Id">Row id.</param>
/// <param name="AgentId">Agent that took the action.</param>
/// <param name="Platform">Platform key from the policy matrix.</param>
/// <param name="Action">Outward action name, for example <c>Post</c> or <c>Withdraw</c>.</param>
/// <param name="TargetUrl">Target of the action; for a withdrawal, the withdrawn item's external id.</param>
/// <param name="BodySha256">Lowercase hex SHA-256 of the body, or null when the action had no body.</param>
/// <param name="Status">Outcome of the action, for example <c>done</c>.</param>
/// <param name="CreatedAt">Time the row was written.</param>
/// <param name="ApprovalId">Approval that allowed the action, if one was needed.</param>
/// <param name="ExternalId">Platform id of the item created or changed, if known.</param>
public sealed record CommunityLogRowDto(
    long Id,
    string AgentId,
    string Platform,
    string Action,
    string? TargetUrl,
    string? BodySha256,
    string Status,
    DateTimeOffset CreatedAt,
    string? ApprovalId,
    string? ExternalId);

/// <summary>One page of log rows, newest first.</summary>
/// <param name="Rows">Rows of this page.</param>
/// <param name="NextCursor">Opaque cursor for the next (older) page, or null when this is the last page.</param>
public sealed record CommunityLogPageDto(IReadOnlyList<CommunityLogRowDto> Rows, string? NextCursor);

/// <summary>Query for a page of the community log. A null filter matches all values.</summary>
/// <param name="AgentId">Only rows for this agent, or null for all agents.</param>
/// <param name="Platform">Only rows for this platform, or null for all platforms.</param>
/// <param name="Limit">Page size; the server clamps it to its own range.</param>
/// <param name="Cursor">The <see cref="CommunityLogPageDto.NextCursor"/> of the previous page, or null for the first page.</param>
public sealed record CommunityLogQueryDto(string? AgentId, string? Platform, int Limit, string? Cursor = null);
