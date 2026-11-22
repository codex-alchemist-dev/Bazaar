using Bazaar.Roles.Abstractions;

namespace Bazaar.Roles.Tests;

/// <summary>Single-threaded store with the same contract as the Postgres one, for the domain tests.</summary>
internal sealed class InMemoryRoleStore : IRoleStore
{
    private readonly Dictionary<Guid, Role> _roles = [];
    private readonly List<RoleAssignment> _assignments = [];

    public Task<Role?> GetAsync(Guid roleId, CancellationToken ct = default) =>
        Task.FromResult(_roles.GetValueOrDefault(roleId));

    public Task<Role?> GetByTemplateAsync(string templateKey, CancellationToken ct = default) =>
        Task.FromResult(_roles.Values.FirstOrDefault(r => r.TemplateKey == templateKey));

    public Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Role>>(_roles.Values.ToList());

    public Task<bool> CreateAsync(Role role, CancellationToken ct = default)
    {
        if (_roles.Values.Any(r => string.Equals(r.Name, role.Name, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(false);
        _roles[role.Id] = role;
        return Task.FromResult(true);
    }

    public Task<bool> ReplaceGrantsAsync(Guid roleId, IReadOnlyList<Grant> grants, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        if (!_roles.TryGetValue(roleId, out var role))
            return Task.FromResult(false);
        _roles[roleId] = role with { Grants = grants, UpdatedAt = updatedAt };
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid roleId, CancellationToken ct = default)
    {
        _assignments.RemoveAll(a => a.RoleId == roleId);
        return Task.FromResult(_roles.Remove(roleId));
    }

    public Task<bool> AssignAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        if (_assignments.Any(a => a.PrincipalId == assignment.PrincipalId && a.RoleId == assignment.RoleId))
            return Task.FromResult(false);
        _assignments.Add(assignment);
        return Task.FromResult(true);
    }

    public Task<UnassignResult> UnassignAsync(Guid principalId, Guid roleId, bool keepOneHuman, CancellationToken ct = default)
    {
        var humans = _assignments.Where(a => a.RoleId == roleId && a.PrincipalKind == PrincipalKind.Human).ToList();
        if (keepOneHuman && humans.Count == 1 && humans[0].PrincipalId == principalId)
            return Task.FromResult(UnassignResult.WouldRemoveLastHuman);
        return Task.FromResult(_assignments.RemoveAll(a => a.PrincipalId == principalId && a.RoleId == roleId) > 0
            ? UnassignResult.Removed
            : UnassignResult.NotAssigned);
    }

    public Task<IReadOnlyList<RoleAssignment>> AssignmentsForPrincipalAsync(Guid principalId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RoleAssignment>>(_assignments.Where(a => a.PrincipalId == principalId).ToList());

    public Task<IReadOnlyList<RoleAssignment>> AssignmentsForRoleAsync(Guid roleId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RoleAssignment>>(_assignments.Where(a => a.RoleId == roleId).ToList());

    public Task<bool> AssignIfNoHumanHoldsAsync(RoleAssignment assignment, CancellationToken ct = default)
    {
        if (_assignments.Any(a => a.RoleId == assignment.RoleId && a.PrincipalKind == PrincipalKind.Human))
            return Task.FromResult(false);
        _assignments.Add(assignment);
        return Task.FromResult(true);
    }
}
