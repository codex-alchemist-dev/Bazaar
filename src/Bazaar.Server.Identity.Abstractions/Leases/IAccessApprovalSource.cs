using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// Supplies an owner approval for a Tier 2 request (doc 14, "Approval"). The approval service (doc 15) owns approvals
/// and their expiry; this seam only reports one that is already granted and unexpired for the request.
/// </summary>
public interface IAccessApprovalSource
{
    /// <summary>Returns the approval id granted for this request, or null when none has been given.</summary>
    Task<Guid?> FindApprovedAsync(AccessRequest request, CancellationToken ct = default);
}

/// <summary>Default approval source: no approval is ever granted, so every Tier 2 request needs one first.</summary>
public sealed class NoAccessApprovals : IAccessApprovalSource
{
    /// <inheritdoc />
    public Task<Guid?> FindApprovedAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<Guid?>(null);
    }
}
