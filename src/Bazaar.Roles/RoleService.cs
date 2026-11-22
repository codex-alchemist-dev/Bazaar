using System.Text;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Roles;

/// <summary>
/// Role management with the floors of doc 48 section 4.1: at least one human owner, agents never hold admin or approval,
/// only a human owner grants approval and never to themselves, widening a human's grants goes through the gate, and every
/// change is audited. A refused change throws <see cref="RoleFloorException"/> and changes nothing.
/// </summary>
public sealed class RoleService(
    IRoleStore store,
    IPermissionChecker checker,
    IGrantWideningGate gate,
    IAuditLog audit,
    TimeProvider clock,
    IScopeContainment? containment = null) : IRoleService
{
    private const int _maxNameLength = 100;
    private const int _maxDescriptionLength = 1000;

    public async Task EnsureBuiltInTemplatesAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        foreach (var t in BuiltInRoleTemplates.All)
        {
            if (await store.GetByTemplateAsync(t.Key, ct) is not null)
                continue;
            var role = new Role(Guid.NewGuid(), t.Name, t.Description, t.Key, t.Protected, t.Grants, now, now);
            if (!await store.CreateAsync(role, ct))
            {
                // A custom role already uses the name: keep the template under a distinguishable one.
                if (!await store.CreateAsync(role with { Name = t.Name + " (built-in)" }, ct))
                    continue;
            }

            await AuditAsync("system", "roles.template-created", w =>
            {
                w.WriteString("role", role.Id.ToString("D"));
                w.WriteString("template", t.Key);
            }, ct);
        }
    }

    public async Task BootstrapOwnerAsync(Guid principalId, CancellationToken ct = default)
    {
        await EnsureBuiltInTemplatesAsync(ct);
        var owner = await store.GetByTemplateAsync(RoleTemplateKeys.Owner, ct)
            ?? throw new RoleFloorException(RoleRefusal.Invalid, "The owner role does not exist.");
        var assignment = new RoleAssignment(principalId, PrincipalKind.Human, owner.Id, principalId, clock.GetUtcNow());
        if (!await store.AssignIfNoHumanHoldsAsync(assignment, ct))
            throw new RoleFloorException(RoleRefusal.Invalid, "A human owner already exists.");
        await AuditAsync(principalId.ToString("D"), "roles.owner-bootstrapped", w => w.WriteString("principal", principalId.ToString("D")), ct);
    }

    public async Task<Role> CreateRoleAsync(RoleActor actor, string name, string description, IReadOnlyList<Grant> grants, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        name = (name ?? "").Trim();
        description = (description ?? "").Trim();
        if (name.Length == 0 || name.Length > _maxNameLength || description.Length > _maxDescriptionLength)
            throw new RoleFloorException(RoleRefusal.Invalid, "The role name must be 1 to 100 characters and the description at most 1000.");
        grants = grants.Distinct().ToList();
        if (grants.Any(g => g.Capability.IsApproval))
            await RequireApprovalGrantorAsync(actor, null, ct);

        var now = clock.GetUtcNow();
        var role = new Role(Guid.NewGuid(), name, description, null, false, grants, now, now);
        if (!await store.CreateAsync(role, ct))
            throw new RoleFloorException(RoleRefusal.Invalid, $"A role named '{name}' already exists.");
        await AuditAsync(actor.PrincipalId.ToString("D"), "roles.created", w =>
        {
            w.WriteString("role", role.Id.ToString("D"));
            w.WriteString("name", name);
            WriteGrants(w, "added", grants);
        }, ct);
        return role;
    }

    public async Task<Role> ReplaceGrantsAsync(RoleActor actor, Guid roleId, IReadOnlyList<Grant> grants, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        var role = await store.GetAsync(roleId, ct) ?? throw new RoleFloorException(RoleRefusal.Invalid, "The role does not exist.");
        grants = grants.Distinct().ToList();

        if (role.Protected && BuiltInRoleTemplates.OwnerFloor.Any(f => !grants.Any(g => g.Covers(f, containment))))
            throw new RoleFloorException(RoleRefusal.ProtectedRole, "The owner role keeps admin.roles and approve at instance scope.");

        var added = grants.Where(g => !role.Grants.Any(old => old.Covers(g, containment))).ToList();
        var assignees = await store.AssignmentsForRoleAsync(roleId, ct);

        if (added.Any(g => g.Capability.IsAdmin || g.Capability.IsApproval) && assignees.Any(a => a.PrincipalKind != PrincipalKind.Human))
            throw new RoleFloorException(RoleRefusal.AgentCapability, "Agents and services never hold admin.* or approve.");
        if (added.Any(g => g.Capability.IsApproval))
            await RequireApprovalGrantorAsync(actor, assignees.Select(a => a.PrincipalId), ct);

        if (added.Count > 0 && assignees.Any(a => a.PrincipalKind == PrincipalKind.Human))
        {
            await RequireWideningApprovedAsync(
                new GrantWideningRequest(actor, $"Widen role '{role.Name}'", roleId, null, added), ct);
        }

        if (!await store.ReplaceGrantsAsync(roleId, grants, clock.GetUtcNow(), ct))
            throw new RoleFloorException(RoleRefusal.Invalid, "The role does not exist.");

        var removed = role.Grants.Where(old => !grants.Any(g => g.Covers(old, containment))).ToList();
        await AuditAsync(actor.PrincipalId.ToString("D"), "roles.grants-replaced", w =>
        {
            w.WriteString("role", roleId.ToString("D"));
            WriteGrants(w, "added", added);
            WriteGrants(w, "removed", removed);
        }, ct);
        return (await store.GetAsync(roleId, ct))!;
    }

    public async Task DeleteRoleAsync(RoleActor actor, Guid roleId, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        var role = await store.GetAsync(roleId, ct) ?? throw new RoleFloorException(RoleRefusal.Invalid, "The role does not exist.");
        if (role.Protected)
            throw new RoleFloorException(RoleRefusal.ProtectedRole, "The owner role cannot be deleted.");
        await store.DeleteAsync(roleId, ct);
        await AuditAsync(actor.PrincipalId.ToString("D"), "roles.deleted", w =>
        {
            w.WriteString("role", roleId.ToString("D"));
            w.WriteString("name", role.Name);
        }, ct);
    }

    public async Task AssignAsync(RoleActor actor, Guid principalId, PrincipalKind principalKind, Guid roleId, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        var role = await store.GetAsync(roleId, ct) ?? throw new RoleFloorException(RoleRefusal.Invalid, "The role does not exist.");

        var privileged = role.Grants.Where(g => g.Capability.IsAdmin || g.Capability.IsApproval).ToList();
        if (principalKind != PrincipalKind.Human && privileged.Count > 0)
            throw new RoleFloorException(RoleRefusal.AgentCapability, "Agents and services never hold admin.* or approve.");
        if (role.Grants.Any(g => g.Capability.IsApproval))
            await RequireApprovalGrantorAsync(actor, [principalId], ct);

        if (principalKind == PrincipalKind.Human)
        {
            var held = new List<Grant>();
            foreach (var a in await store.AssignmentsForPrincipalAsync(principalId, ct))
            {
                if (a.RoleId != roleId && await store.GetAsync(a.RoleId, ct) is { } other)
                    held.AddRange(other.Grants);
            }

            var added = role.Grants.Where(g => !held.Any(h => h.Covers(g, containment))).ToList();
            if (added.Count > 0)
            {
                await RequireWideningApprovedAsync(
                    new GrantWideningRequest(actor, $"Give role '{role.Name}' to a person", roleId, principalId, added), ct);
            }
        }

        var assignment = new RoleAssignment(principalId, principalKind, roleId, actor.PrincipalId, clock.GetUtcNow());
        if (!await store.AssignAsync(assignment, ct))
            return; // already held: nothing changed, nothing to audit
        await AuditAsync(actor.PrincipalId.ToString("D"), "roles.assigned", w =>
        {
            w.WriteString("principal", principalId.ToString("D"));
            w.WriteString("kind", principalKind.ToString());
            w.WriteString("role", roleId.ToString("D"));
        }, ct);
    }

    public async Task UnassignAsync(RoleActor actor, Guid principalId, Guid roleId, CancellationToken ct = default)
    {
        await RequireAdminAsync(actor, ct);
        var role = await store.GetAsync(roleId, ct) ?? throw new RoleFloorException(RoleRefusal.Invalid, "The role does not exist.");
        var result = await store.UnassignAsync(principalId, roleId, keepOneHuman: role.Protected, ct);
        if (result == UnassignResult.WouldRemoveLastHuman)
            throw new RoleFloorException(RoleRefusal.LastOwner, "At least one human must keep the owner role.");
        if (result == UnassignResult.NotAssigned)
            return;
        await AuditAsync(actor.PrincipalId.ToString("D"), "roles.unassigned", w =>
        {
            w.WriteString("principal", principalId.ToString("D"));
            w.WriteString("role", roleId.ToString("D"));
        }, ct);
    }

    private async Task RequireAdminAsync(RoleActor actor, CancellationToken ct)
    {
        if (!await checker.IsAllowedAsync(actor.PrincipalId, actor.Kind, Capabilities.AdminRoles, Scope.Instance, ct))
            throw new RoleFloorException(RoleRefusal.NotAuthorized, "Changing roles needs admin.roles.");
    }

    /// <summary>Only a human who holds the approval capability grants it, and never to themselves.</summary>
    private async Task RequireApprovalGrantorAsync(RoleActor actor, IEnumerable<Guid>? targets, CancellationToken ct)
    {
        if (actor.Kind != PrincipalKind.Human
            || !await checker.IsAllowedAsync(actor.PrincipalId, actor.Kind, Capabilities.Approve, Scope.Instance, ct)
            || (targets?.Contains(actor.PrincipalId) ?? false))
        {
            throw new RoleFloorException(RoleRefusal.ApprovalGrant, "Only a human owner other than the target grants the approval capability.");
        }
    }

    private async Task RequireWideningApprovedAsync(GrantWideningRequest request, CancellationToken ct)
    {
        if (!await gate.ApproveAsync(request, ct))
            throw new RoleFloorException(RoleRefusal.WideningNotApproved, "Widening a person's grants needs the owner's signed approval and a biometric check.");
    }

    private Task AuditAsync(string actor, string kind, Action<Utf8JsonWriter> body, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return audit.AppendAsync(new AuditEntry(clock.GetUtcNow(), actor, kind, Encoding.UTF8.GetString(stream.ToArray())), ct);
    }

    private static void WriteGrants(Utf8JsonWriter w, string name, IReadOnlyList<Grant> grants)
    {
        w.WriteStartArray(name);
        foreach (var g in grants)
            w.WriteStringValue($"{g.Capability.Value}@{g.Scope.Kind}{(g.Scope.Id.Length == 0 ? "" : ":" + g.Scope.Id)}");
        w.WriteEndArray();
    }
}
