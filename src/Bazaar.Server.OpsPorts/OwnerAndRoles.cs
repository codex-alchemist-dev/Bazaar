using Bazaar.Client.Tui.Dashboard;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.OpsPorts;

/// <summary>
/// The owner's local gate (doc 58 section 5, doc 45 section 1.9): the same rule as <c>bazaar-admin</c>. Being on the box is the proof:
/// the process reached the database through the operator's own connection reference, which only the box's accounts can read. The
/// host tells the gate that fact through <paramref name="isOnBox"/>. The gate then names the owner from the role store: the one human
/// holding the owner role, or the principal the operator pinned with <c>Bazaar:Ops:OwnerPrincipalId</c> when several do. Anything
/// unclear (no owner yet, several owners and none pinned, the pinned one not an owner) leaves the gate closed.
/// </summary>
public sealed class RoleStoreOwnerLocalGate(IRoleStore roles, Func<bool> isOnBox, Guid? pinnedOwner = null) : IOwnerLocalGate
{
    /// <summary>The device id of a console session on the box. It is not a registered device, so a device revocation never names it.</summary>
    public static readonly Guid LocalConsoleDevice = Guid.Empty;

    public async Task<AdminIdentity?> EnterAsync(CancellationToken ct = default)
    {
        if (!isOnBox())
        {
            return null;
        }

        var owners = await OwnersAsync(ct).ConfigureAwait(false);
        if (pinnedOwner is { } pinned)
        {
            return owners.Contains(pinned) ? Identity(pinned) : null;
        }

        return owners.Count == 1 ? Identity(owners[0]) : null;
    }

    /// <summary>True when at least one human holds the owner role. The TUI starts in Install mode when this is false (doc 58 section 2).</summary>
    public async Task<bool> OwnerExistsAsync(CancellationToken ct = default) =>
        (await OwnersAsync(ct).ConfigureAwait(false)).Count > 0;

    private async Task<IReadOnlyList<Guid>> OwnersAsync(CancellationToken ct)
    {
        var role = await roles.GetByTemplateAsync(RoleTemplateKeys.Owner, ct).ConfigureAwait(false);
        if (role is null)
        {
            return [];
        }

        var assignments = await roles.AssignmentsForRoleAsync(role.Id, ct).ConfigureAwait(false);
        return assignments
            .Where(a => a.PrincipalKind == PrincipalKind.Human)
            .Select(a => a.PrincipalId)
            .Distinct()
            .ToList();
    }

    private static AdminIdentity Identity(Guid principal) => new(principal, PrincipalKind.Human, LocalConsoleDevice);
}

/// <summary>
/// The roles check for a code-redeemed admin (doc 58 section 5, "under the same roles check"): a human who holds any capability the
/// web admin's overview accepts, at instance scope. It is the web admin's list (<see cref="AdminGate.CapabilitiesFor"/>), not a copy.
/// Agents and services never use Ops.
/// </summary>
public sealed class RolesOpsAuthorizer(IPermissionChecker checker) : IOpsAuthorizer
{
    public async Task<bool> MayUseOpsAsync(AdminIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != PrincipalKind.Human)
        {
            return false;
        }

        foreach (var capability in AdminGate.CapabilitiesFor(AdminArea.Overview))
        {
            if (await checker.IsAllowedAsync(identity.PrincipalId, identity.Kind, capability, Scope.Instance, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }
}
