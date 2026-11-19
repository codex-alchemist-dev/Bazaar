using Bazaar.Contracts.Hive;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>
/// Default <see cref="IHiveGrantWireHandler"/>. It checks the wire type, the owner gate and the request, then queues the change
/// through <see cref="IHiveGrantApprovals"/>. It never applies a change itself, so an accepted reply always has
/// <c>ApprovalRequired = true</c>.
/// </summary>
public sealed class HiveGrantWireHandler(IHiveGrantApprovals approvals) : IHiveGrantWireHandler
{
    public async Task<HiveGrantChangeReply> HandleAsync(string wireType, HiveGrantChangeRequest request, HiveCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        var action = wireType switch
        {
            WireTypes.HiveGrantSet => HiveGrantAction.Set,
            WireTypes.HiveGrantRevoke => HiveGrantAction.Revoke,
            _ => (HiveGrantAction?)null,
        };
        if (action is not { } act)
            return Refuse("unknown wire type");
        if (!caller.IsOwner)
            return Refuse("owner only");

        var change = new HiveGrantChange(act, new ProjectId(request.FromProject), new ProjectId(request.ToProject), request.Kind);
        try
        {
            HiveGrantRules.ValidatePair(change.FromProject, change.ToProject, change.Kind);
            if (string.IsNullOrWhiteSpace(caller.Principal))
                throw new ArgumentException(act == HiveGrantAction.Set ? "A grant records who granted it." : "A revoke records who revoked it.");
        }
        catch (ArgumentException ex)
        {
            return Refuse(ex.Message);
        }

        var ticket = await approvals.RequestAsync(change, caller.Principal, ct).ConfigureAwait(false);
        return ticket.Accepted
            ? new HiveGrantChangeReply(Accepted: true, Reason: null, ApprovalRequired: true)
            : Refuse(ticket.Reason ?? "approval refused");
    }

    private static HiveGrantChangeReply Refuse(string reason) => new(Accepted: false, Reason: reason, ApprovalRequired: false);
}
