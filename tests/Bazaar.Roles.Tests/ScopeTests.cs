using Bazaar.Roles.Abstractions;

namespace Bazaar.Roles.Tests;

public sealed class ScopeTests
{
    private sealed class TagContainment : IScopeContainment
    {
        public bool Contains(Scope outer, Scope inner) =>
            outer == new Scope(ScopeKind.GcTag, "ops") && inner == Scope.Gc("g1");
    }

    [Fact]
    public void Instance_covers_everything_and_company_covers_all_but_the_instance()
    {
        Assert.True(Scope.Instance.Covers(Scope.Company));
        Assert.True(Scope.Instance.Covers(Scope.Gc("a")));
        Assert.True(Scope.Company.Covers(Scope.Gc("a")));
        Assert.True(Scope.Company.Covers(Scope.Module("m")));
        Assert.False(Scope.Company.Covers(Scope.Instance));
    }

    [Fact]
    public void A_narrow_scope_covers_itself_and_what_containment_reports()
    {
        var tag = new Scope(ScopeKind.GcTag, "ops");
        Assert.True(Scope.Gc("a").Covers(Scope.Gc("a")));
        Assert.False(Scope.Gc("a").Covers(Scope.Gc("b")));
        Assert.False(tag.Covers(Scope.Gc("g1")));
        Assert.True(tag.Covers(Scope.Gc("g1"), new TagContainment()));
    }

    [Fact]
    public void Wide_scopes_take_no_id_and_narrow_ones_need_one()
    {
        Assert.Throws<ArgumentException>(() => new Scope(ScopeKind.Instance, "x"));
        Assert.Throws<ArgumentException>(() => new Scope(ScopeKind.Gc, ""));
    }

    [Theory]
    [InlineData("read", true)]
    [InlineData("admin.roles", true)]
    [InlineData("install-package", true)]
    [InlineData("Read", false)]
    [InlineData("admin.", false)]
    [InlineData("", false)]
    public void Capability_names_are_validated(string name, bool valid)
    {
        if (valid)
            Assert.Equal(name, new Capability(name).Value);
        else
            Assert.Throws<ArgumentException>(() => new Capability(name));
    }

    [Fact]
    public void Templates_stay_within_the_known_capabilities_and_agents_get_no_admin()
    {
        var agent = BuiltInRoleTemplates.All.Single(t => t.Key == RoleTemplateKeys.Agent);
        Assert.DoesNotContain(agent.Grants, g => g.Capability.IsAdmin || g.Capability.IsApproval);
        Assert.Equal(5, BuiltInRoleTemplates.All.Count);
        Assert.Single(BuiltInRoleTemplates.All, t => t.Protected);
    }
}
