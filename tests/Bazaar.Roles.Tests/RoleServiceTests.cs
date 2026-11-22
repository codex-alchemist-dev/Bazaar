using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Roles.Tests;

public sealed class RoleServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowGate(bool allow) : IGrantWideningGate
    {
        public List<GrantWideningRequest> Seen { get; } = [];

        public Task<bool> ApproveAsync(GrantWideningRequest request, CancellationToken ct = default)
        {
            Seen.Add(request);
            return Task.FromResult(allow);
        }
    }

    private sealed class World
    {
        public InMemoryRoleStore Store { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public AllowGate Gate { get; }
        public RoleService Service { get; }
        public Guid Owner { get; } = Guid.NewGuid();

        public World(bool gateAllows = true)
        {
            Gate = new AllowGate(gateAllows);
            Service = new RoleService(Store, new PermissionChecker(Store), Gate, Audit, TimeProvider.System);
        }

        public RoleActor OwnerActor => new(Owner, PrincipalKind.Human);

        public async Task<Role> OwnerRoleAsync() => (await Store.GetByTemplateAsync(RoleTemplateKeys.Owner, Ct))!;

        public async Task<Role> TemplateAsync(string key) => (await Store.GetByTemplateAsync(key, Ct))!;
    }

    private static async Task<World> BootstrappedAsync(bool gateAllows = true)
    {
        var w = new World(gateAllows);
        await w.Service.BootstrapOwnerAsync(w.Owner, Ct);
        return w;
    }

    [Fact]
    public async Task Templates_are_inserted_once_and_owner_edits_survive()
    {
        var w = await BootstrappedAsync();
        var reviewer = await w.TemplateAsync(RoleTemplateKeys.Reviewer);
        await w.Service.ReplaceGrantsAsync(w.OwnerActor, reviewer.Id, [], Ct);

        await w.Service.EnsureBuiltInTemplatesAsync(Ct);

        Assert.Equal(5, (await w.Store.ListAsync(Ct)).Count);
        Assert.Empty((await w.TemplateAsync(RoleTemplateKeys.Reviewer)).Grants);
    }

    [Fact]
    public async Task Bootstrap_works_once()
    {
        var w = await BootstrappedAsync();
        var ex = await Assert.ThrowsAsync<RoleFloorException>(() => w.Service.BootstrapOwnerAsync(Guid.NewGuid(), Ct));
        Assert.Equal(RoleRefusal.Invalid, ex.Refusal);
    }

    [Fact]
    public async Task The_owner_may_change_roles_and_a_plain_coworker_may_not()
    {
        var w = await BootstrappedAsync();
        var coworker = Guid.NewGuid();
        await w.Service.AssignAsync(w.OwnerActor, coworker, PrincipalKind.Human, (await w.TemplateAsync(RoleTemplateKeys.Coworker)).Id, Ct);

        var ex = await Assert.ThrowsAsync<RoleFloorException>(() =>
            w.Service.CreateRoleAsync(new RoleActor(coworker, PrincipalKind.Human), "x", "", [], Ct));

        Assert.Equal(RoleRefusal.NotAuthorized, ex.Refusal);
    }

    [Fact]
    public async Task The_last_human_owner_cannot_be_removed()
    {
        var w = await BootstrappedAsync();
        var owner = await w.OwnerRoleAsync();

        var ex = await Assert.ThrowsAsync<RoleFloorException>(() => w.Service.UnassignAsync(w.OwnerActor, w.Owner, owner.Id, Ct));

        Assert.Equal(RoleRefusal.LastOwner, ex.Refusal);
        Assert.Single(await w.Store.AssignmentsForRoleAsync(owner.Id, Ct));
    }

    [Fact]
    public async Task A_second_owner_makes_removal_possible_and_it_is_audited()
    {
        var w = await BootstrappedAsync();
        var owner = await w.OwnerRoleAsync();
        var second = Guid.NewGuid();
        await w.Service.AssignAsync(w.OwnerActor, second, PrincipalKind.Human, owner.Id, Ct);

        await w.Service.UnassignAsync(w.OwnerActor, second, owner.Id, Ct);

        Assert.Contains(w.Audit.Entries, e => e.Kind == "roles.unassigned");
        Assert.Contains(w.Audit.Entries, e => e.Kind == "roles.assigned");
    }

    [Fact]
    public async Task An_agent_never_gets_a_role_with_admin_or_approve()
    {
        var w = await BootstrappedAsync();
        var owner = await w.OwnerRoleAsync();

        var ex = await Assert.ThrowsAsync<RoleFloorException>(() =>
            w.Service.AssignAsync(w.OwnerActor, Guid.NewGuid(), PrincipalKind.Agent, owner.Id, Ct));

        Assert.Equal(RoleRefusal.AgentCapability, ex.Refusal);
    }

    [Fact]
    public async Task Adding_admin_to_a_role_an_agent_holds_is_refused()
    {
        var w = await BootstrappedAsync();
        var agentRole = await w.TemplateAsync(RoleTemplateKeys.Agent);
        await w.Service.AssignAsync(w.OwnerActor, Guid.NewGuid(), PrincipalKind.Agent, agentRole.Id, Ct);

        var ex = await Assert.ThrowsAsync<RoleFloorException>(() =>
            w.Service.ReplaceGrantsAsync(w.OwnerActor, agentRole.Id, [.. agentRole.Grants, new Grant(Capabilities.AdminModules, Scope.Instance)], Ct));

        Assert.Equal(RoleRefusal.AgentCapability, ex.Refusal);
    }

    [Fact]
    public async Task The_checker_refuses_an_agent_admin_even_when_rows_grant_it()
    {
        var store = new InMemoryRoleStore();
        var agent = Guid.NewGuid();
        var role = new Role(Guid.NewGuid(), "bad", "", null, false, [new Grant(Capabilities.AdminModules, Scope.Instance)], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await store.CreateAsync(role, Ct);
        await store.AssignAsync(new RoleAssignment(agent, PrincipalKind.Agent, role.Id, agent, DateTimeOffset.UtcNow), Ct);

        var allowed = await new PermissionChecker(store).IsAllowedAsync(agent, PrincipalKind.Agent, Capabilities.AdminModules, Scope.Instance, Ct);

        Assert.False(allowed);
    }

    [Fact]
    public async Task Approve_cannot_be_given_to_oneself_or_by_a_non_approver()
    {
        var w = await BootstrappedAsync();
        var owner = await w.OwnerRoleAsync();

        // Self-grant: the actor is the target.
        var self = await Assert.ThrowsAsync<RoleFloorException>(() =>
            w.Service.AssignAsync(w.OwnerActor, w.Owner, PrincipalKind.Human, owner.Id, Ct));
        Assert.Equal(RoleRefusal.ApprovalGrant, self.Refusal);

        // An admin.roles holder without approve cannot hand it out.
        var admin = await w.Service.CreateRoleAsync(w.OwnerActor, "Role admin", "", [new Grant(Capabilities.AdminRoles, Scope.Instance)], Ct);
        var roleAdmin = Guid.NewGuid();
        await w.Service.AssignAsync(w.OwnerActor, roleAdmin, PrincipalKind.Human, admin.Id, Ct);
        var other = await Assert.ThrowsAsync<RoleFloorException>(() =>
            w.Service.AssignAsync(new RoleActor(roleAdmin, PrincipalKind.Human), Guid.NewGuid(), PrincipalKind.Human, owner.Id, Ct));
        Assert.Equal(RoleRefusal.ApprovalGrant, other.Refusal);
    }

    [Fact]
    public async Task The_owner_role_is_protected()
    {
        var w = await BootstrappedAsync();
        var owner = await w.OwnerRoleAsync();

        var delete = await Assert.ThrowsAsync<RoleFloorException>(() => w.Service.DeleteRoleAsync(w.OwnerActor, owner.Id, Ct));
        var strip = await Assert.ThrowsAsync<RoleFloorException>(() =>
            w.Service.ReplaceGrantsAsync(w.OwnerActor, owner.Id, [new Grant(Capabilities.Read, Scope.Company)], Ct));

        Assert.Equal(RoleRefusal.ProtectedRole, delete.Refusal);
        Assert.Equal(RoleRefusal.ProtectedRole, strip.Refusal);
    }

    [Fact]
    public async Task Widening_a_person_needs_the_gate_and_the_default_gate_refuses()
    {
        var closed = await BootstrappedAsync(gateAllows: false);
        var coworker = Guid.NewGuid();
        var role = await closed.TemplateAsync(RoleTemplateKeys.Coworker);

        var ex = await Assert.ThrowsAsync<RoleFloorException>(() =>
            closed.Service.AssignAsync(closed.OwnerActor, coworker, PrincipalKind.Human, role.Id, Ct));

        Assert.Equal(RoleRefusal.WideningNotApproved, ex.Refusal);
        Assert.Empty(await closed.Store.AssignmentsForPrincipalAsync(coworker, Ct));
        var request = Assert.Single(closed.Gate.Seen);
        Assert.Equal(coworker, request.PrincipalId);
        Assert.Contains(request.Added, g => g.Capability == Capabilities.Send);
    }

    [Fact]
    public async Task Giving_a_person_grants_they_already_hold_does_not_ask_the_gate()
    {
        var w = await BootstrappedAsync();
        var coworker = Guid.NewGuid();
        await w.Service.AssignAsync(w.OwnerActor, coworker, PrincipalKind.Human, (await w.TemplateAsync(RoleTemplateKeys.Coworker)).Id, Ct);
        var asks = w.Gate.Seen.Count;

        await w.Service.AssignAsync(w.OwnerActor, coworker, PrincipalKind.Human, (await w.TemplateAsync(RoleTemplateKeys.ReadOnlyObserver)).Id, Ct);

        Assert.Equal(asks, w.Gate.Seen.Count);
    }

    [Fact]
    public async Task Narrowing_a_role_does_not_ask_the_gate()
    {
        var w = await BootstrappedAsync();
        var coworker = await w.TemplateAsync(RoleTemplateKeys.Coworker);
        await w.Service.AssignAsync(w.OwnerActor, Guid.NewGuid(), PrincipalKind.Human, coworker.Id, Ct);
        var asks = w.Gate.Seen.Count;

        await w.Service.ReplaceGrantsAsync(w.OwnerActor, coworker.Id, [new Grant(Capabilities.Read, Scope.Company)], Ct);

        Assert.Equal(asks, w.Gate.Seen.Count);
        Assert.Contains(w.Audit.Entries, e => e.Kind == "roles.grants-replaced");
    }

    [Fact]
    public async Task Role_names_are_unique_without_case_and_validated()
    {
        var w = await BootstrappedAsync();
        await w.Service.CreateRoleAsync(w.OwnerActor, "Triage", "", [], Ct);

        var dup = await Assert.ThrowsAsync<RoleFloorException>(() => w.Service.CreateRoleAsync(w.OwnerActor, "triage", "", [], Ct));
        var empty = await Assert.ThrowsAsync<RoleFloorException>(() => w.Service.CreateRoleAsync(w.OwnerActor, "  ", "", [], Ct));

        Assert.Equal(RoleRefusal.Invalid, dup.Refusal);
        Assert.Equal(RoleRefusal.Invalid, empty.Refusal);
    }

    [Fact]
    public async Task A_role_change_takes_effect_at_the_next_check()
    {
        var w = await BootstrappedAsync();
        var checker = new PermissionChecker(w.Store);
        var person = Guid.NewGuid();
        var role = await w.Service.CreateRoleAsync(w.OwnerActor, "Poster", "", [new Grant(Capabilities.Post, Scope.Gc("g1"))], Ct);
        await w.Service.AssignAsync(w.OwnerActor, person, PrincipalKind.Human, role.Id, Ct);

        Assert.True(await checker.IsAllowedAsync(person, PrincipalKind.Human, Capabilities.Post, Scope.Gc("g1"), Ct));
        Assert.False(await checker.IsAllowedAsync(person, PrincipalKind.Human, Capabilities.Post, Scope.Gc("g2"), Ct));

        await w.Service.UnassignAsync(w.OwnerActor, person, role.Id, Ct);

        Assert.False(await checker.IsAllowedAsync(person, PrincipalKind.Human, Capabilities.Post, Scope.Gc("g1"), Ct));
    }
}
