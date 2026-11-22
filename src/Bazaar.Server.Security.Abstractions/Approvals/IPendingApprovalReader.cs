namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// Read-only view of the owner's approval queue (doc 15): requests still waiting for a decision. Implemented by the approval stores.
/// Rows past their expiry are not returned, even before anything has marked them expired.
/// </summary>
public interface IPendingApprovalReader
{
    /// <summary>Pending, unexpired requests at <paramref name="now"/>, oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken ct = default);
}
