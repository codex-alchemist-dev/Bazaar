using Bazaar.Roles.Abstractions;

namespace Bazaar.Roles;

/// <summary>Resolves a principal's roles from the store on every call, so a change applies at the next request.</summary>
public sealed class PermissionChecker(IRoleStore store, IScopeContainment? containment = null) : IPermissionChecker
{
    public async Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default)
    {
        if (kind == PrincipalKind.Agent && (capability.IsAdmin || capability.IsApproval))
            return false;

        foreach (var assignment in await store.AssignmentsForPrincipalAsync(principalId, ct))
        {
            var role = await store.GetAsync(assignment.RoleId, ct);
            if (role is null)
                continue;
            foreach (var grant in role.Grants)
            {
                if (grant.Capability == capability && grant.Scope.Covers(scope, containment))
                    return true;
            }
        }

        return false;
    }
}
