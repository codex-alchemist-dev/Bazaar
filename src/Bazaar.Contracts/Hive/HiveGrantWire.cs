using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Hive;

/// <summary>Payload of the <c>hive.grant.set</c> and <c>hive.grant.revoke</c> requests: the one grant to change.</summary>
/// <param name="FromProject">Project the lessons flow out of.</param>
/// <param name="ToProject">Project the copies flow into.</param>
/// <param name="Kind">Lesson kind the grant covers.</param>
[WirePayload(WireTypes.HiveGrantSet)]
[WirePayload(WireTypes.HiveGrantRevoke)]
public sealed record HiveGrantChangeRequest(string FromProject, string ToProject, string Kind);

/// <summary>
/// Reply to a grant or revoke. <see cref="ApprovalRequired"/> true means the change waits for owner approval and is not in
/// force; the client must not show it as granted.
/// </summary>
/// <param name="Accepted">True when the server took the request (applied or queued for approval).</param>
/// <param name="Reason">Why the request was refused, or null when accepted.</param>
/// <param name="ApprovalRequired">True when the change is queued for owner approval.</param>
[WirePayload]
public sealed record HiveGrantChangeReply(bool Accepted, string? Reason, bool ApprovalRequired);
