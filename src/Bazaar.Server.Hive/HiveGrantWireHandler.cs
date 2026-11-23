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
    /// <inheritdoc />
    public async Task<HiveGrantChangeReply> HandleAsync(string wireType, HiveGrantChangeRequest request, HiveCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        HiveGrantAction action;
        switch (wireType)
        {
            case WireTypes.HiveGrantSet:
                action = HiveGrantAction.Set;
                break;
            case WireTypes.HiveGrantRevoke:
                action = HiveGrantAction.Revoke;
                break;
            default:
                return Refuse("unknown wire type");
        }

        if (!caller.IsOwner)
        {
            return Refuse("owner only");
        }

        if (!HiveKinds.TryParse(request.Kind, out var kind))
        {
            return Refuse("unknown kind");
        }

        if (string.IsNullOrWhiteSpace(caller.Principal))
        {
            return Refuse(action == HiveGrantAction.Set ? "A grant records who granted it." : "A revoke records who revoked it.");
        }

        var change = new HiveGrantChange(action, request.FromProject, request.ToProject, kind);
        try
        {
            HiveGrantRules.Validate(new HiveGrant(change.FromProject, change.ToProject, change.Kind, caller.Principal));
        }
        catch (ArgumentException)
        {
            return Refuse("A grant needs two different projects.");
        }

        var ticket = await approvals.RequestAsync(change, caller.Principal, ct).ConfigureAwait(false);
        return ticket.Accepted
            ? new HiveGrantChangeReply(Accepted: true, Reason: null, ApprovalRequired: true)
            : Refuse(ticket.Reason ?? "approval refused");
    }

    private static HiveGrantChangeReply Refuse(string reason) => new(Accepted: false, Reason: reason, ApprovalRequired: false);
}
