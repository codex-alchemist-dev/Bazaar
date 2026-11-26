using Bazaar.Roles.Abstractions;
using Bazaar.Roles.Tests;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.OpsPorts.Tests;

public class OwnerGateAndRolesTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private static async Task<(InMemoryRoleStore Store, Guid OwnerRole)> StoreAsync(params (Guid Principal, PrincipalKind Kind)[] owners)
    {
        var store = new InMemoryRoleStore();
        var role = new Role(Guid.NewGuid(), "Owner", "the owner", RoleTemplateKeys.Owner, true, [], _now, _now);
        Assert.True(await store.CreateAsync(role));
        foreach (var (principal, kind) in owners)
        {
            Assert.True(await store.AssignAsync(new RoleAssignment(principal, kind, role.Id, principal, _now)));
        }

        return (store, role.Id);
    }

    [Fact]
    public async Task The_sole_owner_on_the_box_gets_a_human_identity_with_the_console_device()
    {
        var owner = Guid.NewGuid();
        var (store, _) = await StoreAsync((owner, PrincipalKind.Human));

        var identity = await new RoleStoreOwnerLocalGate(store, () => true).EnterAsync();

        Assert.NotNull(identity);
        Assert.Equal(owner, identity.PrincipalId);
        Assert.Equal(PrincipalKind.Human, identity.Kind);
        Assert.Equal(RoleStoreOwnerLocalGate.LocalConsoleDevice, identity.DeviceId);
    }

    [Fact]
    public async Task The_gate_is_closed_when_not_on_the_box()
    {
        var (store, _) = await StoreAsync((Guid.NewGuid(), PrincipalKind.Human));

        Assert.Null(await new RoleStoreOwnerLocalGate(store, () => false).EnterAsync());
    }

    [Fact]
    public async Task The_gate_is_closed_when_no_owner_exists_yet()
    {
        var (store, _) = await StoreAsync();
        var gate = new RoleStoreOwnerLocalGate(store, () => true);

        Assert.Null(await gate.EnterAsync());
        Assert.False(await gate.OwnerExistsAsync());
        Assert.False(await new RoleStoreOwnerLocalGate(new InMemoryRoleStore(), () => true).OwnerExistsAsync());
    }

    [Fact]
    public async Task Several_owners_leave_the_gate_closed_unless_the_operator_pins_one()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var (store, _) = await StoreAsync((first, PrincipalKind.Human), (second, PrincipalKind.Human));

        Assert.Null(await new RoleStoreOwnerLocalGate(store, () => true).EnterAsync());
        Assert.Equal(second, (await new RoleStoreOwnerLocalGate(store, () => true, second).EnterAsync())?.PrincipalId);
        Assert.Null(await new RoleStoreOwnerLocalGate(store, () => true, Guid.NewGuid()).EnterAsync());
    }

    [Fact]
    public async Task An_owner_role_held_by_an_agent_is_not_an_owner()
    {
        var (store, _) = await StoreAsync((Guid.NewGuid(), PrincipalKind.Agent));
        var gate = new RoleStoreOwnerLocalGate(store, () => true);

        Assert.Null(await gate.EnterAsync());
        Assert.False(await gate.OwnerExistsAsync());
    }

    private sealed class Checker(params string[] allowed) : IPermissionChecker
    {
        public List<(Guid Principal, Scope Scope)> Asked { get; } = [];

        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default)
        {
            Asked.Add((principalId, scope));
            return Task.FromResult(allowed.Contains(capability.Value));
        }
    }

    [Theory]
    [InlineData("admin.members")]
    [InlineData("admin.modules")]
    [InlineData("kodo.view.status")]
    public async Task A_human_with_any_admin_capability_at_instance_scope_may_use_ops(string capability)
    {
        var checker = new Checker(capability);

        var allowed = await new RolesOpsAuthorizer(checker).MayUseOpsAsync(new AdminIdentity(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid()));

        Assert.True(allowed);
        Assert.All(checker.Asked, a => Assert.Equal(Scope.Instance, a.Scope));
    }

    [Fact]
    public async Task A_human_with_no_admin_capability_is_refused()
    {
        var allowed = await new RolesOpsAuthorizer(new Checker("kodo.view.audit")).MayUseOpsAsync(new AdminIdentity(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid()));

        Assert.False(allowed);
    }

    [Theory]
    [InlineData(PrincipalKind.Agent)]
    [InlineData(PrincipalKind.Service)]
    public async Task Agents_and_services_never_use_ops_even_when_a_checker_would_allow(PrincipalKind kind)
    {
        var checker = new Checker("admin.members", "admin.modules", "admin.roles", "kodo.view.status");

        var allowed = await new RolesOpsAuthorizer(checker).MayUseOpsAsync(new AdminIdentity(Guid.NewGuid(), kind, Guid.NewGuid()));

        Assert.False(allowed);
        Assert.Empty(checker.Asked);
    }
}
