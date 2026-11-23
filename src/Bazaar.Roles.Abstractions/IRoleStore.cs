namespace Bazaar.Roles.Abstractions;

/// <summary>The outcome of removing an assignment.</summary>
public enum UnassignResult
{
    Removed,
    NotAssigned,

    /// <summary>The removal would leave no human holding the role, and the caller asked for one to remain.</summary>
    WouldRemoveLastHuman,
}

/// <summary>Storage for roles, grants and assignments. The Postgres implementation is in Bazaar.Roles.</summary>
public interface IRoleStore
{
    Task<Role?> GetAsync(Guid roleId, CancellationToken ct = default);

    Task<Role?> GetByTemplateAsync(string templateKey, CancellationToken ct = default);

    Task<IReadOnlyList<Role>> ListAsync(CancellationToken ct = default);

    /// <summary>Adds a role. False when the name is already taken (names are unique, compared without case).</summary>
    Task<bool> CreateAsync(Role role, CancellationToken ct = default);

    /// <summary>Replaces a role's grants and stamps <paramref name="updatedAt"/>. False when the role does not exist.</summary>
    Task<bool> ReplaceGrantsAsync(Guid roleId, IReadOnlyList<Grant> grants, DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>Deletes a role and its assignments. False when it does not exist.</summary>
    Task<bool> DeleteAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>Adds an assignment. False when the principal already holds the role.</summary>
    Task<bool> AssignAsync(RoleAssignment assignment, CancellationToken ct = default);

    /// <summary>
    /// Removes an assignment. When <paramref name="keepOneHuman"/> is set the check and the delete are one atomic step, so two
    /// concurrent removals cannot together empty the role of humans.
    /// </summary>
    Task<UnassignResult> UnassignAsync(Guid principalId, Guid roleId, bool keepOneHuman, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignment>> AssignmentsForPrincipalAsync(Guid principalId, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignment>> AssignmentsForRoleAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>
    /// Assigns <paramref name="assignment"/> only when no human holds <paramref name="roleId"/> yet. Returns false otherwise.
    /// One atomic step, so a first-owner bootstrap cannot race a second one.
    /// </summary>
    Task<bool> AssignIfNoHumanHoldsAsync(RoleAssignment assignment, CancellationToken ct = default);
}
