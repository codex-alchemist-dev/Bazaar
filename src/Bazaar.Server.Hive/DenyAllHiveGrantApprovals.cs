using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>
/// Default <see cref="IHiveGrantApprovals"/> until the approvals module is registered. It refuses every change, so a grant can
/// never be queued or applied by accident (fail closed).
/// </summary>
public sealed class DenyAllHiveGrantApprovals : IHiveGrantApprovals
{
    /// <inheritdoc />
    public Task<HiveApprovalTicket> RequestAsync(HiveGrantChange change, string requestedBy, CancellationToken ct) =>
        Task.FromResult(HiveApprovalTicket.Refused("approvals not configured"));
}
