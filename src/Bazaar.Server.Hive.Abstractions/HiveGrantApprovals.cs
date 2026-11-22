namespace Bazaar.Server.Hive.Abstractions;

/// <summary>The two owner-only grant changes a wire request can ask for.</summary>
public enum HiveGrantAction
{
    /// <summary>Adds a grant.</summary>
    Set = 0,

    /// <summary>Removes a grant and deprecates its copies.</summary>
    Revoke,
}

/// <summary>One grant change as the approvals path sees it.</summary>
/// <param name="Action">Grant or revoke.</param>
/// <param name="FromProject">Source project slug.</param>
/// <param name="ToProject">Target project slug.</param>
/// <param name="Kind">The kind the grant covers.</param>
public sealed record HiveGrantChange(HiveGrantAction Action, string FromProject, string ToProject, HiveKind Kind);

/// <summary>
/// Answer from <see cref="IHiveGrantApprovals.RequestAsync"/>. <see cref="Accepted"/> true means the change is queued for owner
/// approval and is not in force; false carries the refusal in <see cref="Reason"/>.
/// </summary>
/// <param name="Accepted">True when queued.</param>
/// <param name="TicketId">The approval ticket, when queued.</param>
/// <param name="Reason">Why it was refused, or null.</param>
public sealed record HiveApprovalTicket(bool Accepted, Guid? TicketId, string? Reason)
{
    /// <summary>A queued change.</summary>
    public static HiveApprovalTicket Queued(Guid ticketId) => new(true, ticketId, null);

    /// <summary>A refused change.</summary>
    public static HiveApprovalTicket Refused(string reason) => new(false, null, reason);
}

/// <summary>
/// Owner approval for grant changes (doc 25 wire request; approval lives in doc 08 settings and doc 15). The wire handler only
/// queues through this port and never applies a change. The approvals module calls <c>HiveGrantApplier.ApplyApprovedAsync</c> once
/// the owner approves. Throwing means an infrastructure failure, not a refusal.
/// </summary>
public interface IHiveGrantApprovals
{
    /// <summary>Queues a change for owner approval.</summary>
    Task<HiveApprovalTicket> RequestAsync(HiveGrantChange change, string requestedBy, CancellationToken ct);
}
