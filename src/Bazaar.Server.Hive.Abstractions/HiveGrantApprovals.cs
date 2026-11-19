namespace Bazaar.Server.Hive.Abstractions;

/// <summary>The two owner-only grant changes a wire request can ask for.</summary>
public enum HiveGrantAction
{
    Set = 0,
    Revoke,
}

/// <summary>One grant change as the approvals path sees it: the action, the project pair and the lesson kind.</summary>
public sealed record HiveGrantChange(HiveGrantAction Action, ProjectId FromProject, ProjectId ToProject, string Kind);

/// <summary>
/// Answer from <see cref="IHiveGrantApprovals.RequestAsync"/>. <see cref="Accepted"/> true means the change is queued for owner
/// approval and is not in force; false carries the refusal in <see cref="Reason"/>.
/// </summary>
public sealed record HiveApprovalTicket(bool Accepted, Guid? TicketId, string? Reason)
{
    public static HiveApprovalTicket Queued(Guid ticketId) => new(true, ticketId, null);

    public static HiveApprovalTicket Refused(string reason) => new(false, null, reason);
}

/// <summary>
/// Owner approval for grant changes (doc 25 wire request; approval lives in doc 08 settings and doc 15). The handler only queues
/// through this port and never applies a change itself. The approvals module calls <c>HiveGrantService.ApplyApprovedAsync</c>
/// once the owner approves. Throwing means an infrastructure failure, not a refusal.
/// </summary>
public interface IHiveGrantApprovals
{
    Task<HiveApprovalTicket> RequestAsync(HiveGrantChange change, string requestedBy, CancellationToken ct);
}
