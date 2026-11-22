using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.ApprovalQueue;

/// <summary>The admin panel's approvals list (doc 51 section 5) over the queue. Shows kind and subject, never a payload reference.</summary>
public sealed class AdminApprovalSource(IApprovalQueue queue) : IAdminApprovalSource
{
    public async Task<IReadOnlyList<AdminApproval>> ListPendingAsync(CancellationToken ct = default)
    {
        var pending = await queue.ListPendingAsync(null, ct).ConfigureAwait(false);
        return [.. pending.Select(i => new AdminApproval(i.Id.ToString("D"), $"{i.Kind}: {i.Subject}", i.RequesterPrincipalId.ToString("D"), i.RequestedAt))];
    }
}
