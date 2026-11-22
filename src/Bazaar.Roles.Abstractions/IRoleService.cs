namespace Bazaar.Roles.Abstractions;

/// <summary>
/// Role management with the floors of doc 48 section 4.1. Every change is audited with the actor, the diff and the time, and
/// takes effect at the next request. A refused change throws <see cref="RoleFloorException"/> and changes nothing.
/// </summary>
public interface IRoleService
{
    /// <summary>
    /// Inserts any of the five built-in templates that are missing. Existing rows are never touched, so edits the owner made
    /// to a template survive a restart.
    /// </summary>
    Task EnsureBuiltInTemplatesAsync(CancellationToken ct = default);

    /// <summary>Makes <paramref name="principalId"/> the first human owner. Refused when a human owner already exists.</summary>
    Task BootstrapOwnerAsync(Guid principalId, CancellationToken ct = default);

    Task<Role> CreateRoleAsync(RoleActor actor, string name, string description, IReadOnlyList<Grant> grants, CancellationToken ct = default);

    Task<Role> ReplaceGrantsAsync(RoleActor actor, Guid roleId, IReadOnlyList<Grant> grants, CancellationToken ct = default);

    Task DeleteRoleAsync(RoleActor actor, Guid roleId, CancellationToken ct = default);

    Task AssignAsync(RoleActor actor, Guid principalId, PrincipalKind principalKind, Guid roleId, CancellationToken ct = default);

    Task UnassignAsync(RoleActor actor, Guid principalId, Guid roleId, CancellationToken ct = default);
}
