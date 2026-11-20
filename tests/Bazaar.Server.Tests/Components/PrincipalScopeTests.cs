using Cdax.Core;

namespace Bazaar.Server.Tests.Components;

public class PrincipalScopeTests
{
    [Theory]
    [InlineData("user:dom", PrincipalKind.User, "dom")]
    [InlineData("service:kek", PrincipalKind.Service, "kek")]
    [InlineData("system:setup", PrincipalKind.System, "setup")]
    [InlineData("agent:charles-1", PrincipalKind.Agent, "charles-1")]
    public void Principal_round_trips(string text, PrincipalKind kind, string id)
    {
        var p = Principal.Parse(text);

        Assert.Equal(kind, p.Kind);
        Assert.Equal(id, p.Id);
        Assert.Equal(text, p.ToString());
        Assert.Equal(p, Principal.Create(kind, id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user")]
    [InlineData("user:")]
    [InlineData(":dom")]
    [InlineData("User:dom")]
    [InlineData("robot:x")]
    [InlineData("user:a:b")]
    [InlineData("user:a/b")]
    [InlineData("user:a b")]
    [InlineData("user: dom")]
    [InlineData("user:do\tm")]
    [InlineData("user:do\nm")]
    public void Principal_rejects_invalid_text(string? text)
    {
        Assert.False(Principal.TryParse(text, out var p));
        Assert.Null(p);
    }

    [Fact]
    public void Principal_id_length_is_bounded()
    {
        Assert.True(Principal.TryParse("user:" + new string('a', 128), out _));
        Assert.False(Principal.TryParse("user:" + new string('a', 129), out _));
        Assert.Throws<ArgumentException>(() => Principal.User(new string('a', 129)));
        Assert.Throws<FormatException>(() => Principal.Parse("nope"));
    }

    [Fact]
    public void Principal_equality_and_ordering()
    {
        Assert.Equal(Principal.User("dom"), Principal.Parse("user:dom"));
        Assert.True(Principal.User("dom") == Principal.User("dom"));
        Assert.NotEqual(Principal.User("dom"), Principal.Service("dom"));
        Assert.NotEqual(Principal.User("dom"), Principal.User("Dom"));
        Assert.Equal(Principal.User("dom").GetHashCode(), Principal.User("dom").GetHashCode());

        var sorted = new[] { Principal.System("b"), Principal.User("a"), Principal.Agent("z"), Principal.Agent("a"), Principal.Service("a") }
            .Order().Select(p => p.ToString());
        Assert.Equal(["agent:a", "agent:z", "user:a", "service:a", "system:b"], sorted);
        Assert.True(Principal.Agent("a") < Principal.Agent("b"));
        Assert.True(Principal.User("a") >= Principal.Agent("z"));
    }

    [Fact]
    public void Principal_action_carries_actor_and_optional_delegator()
    {
        var self = new PrincipalAction(Principal.Service("kek"));
        var delegated = new PrincipalAction(Principal.Agent("charles"), Principal.User("dom"));

        Assert.Null(self.OnBehalfOf);
        Assert.Equal("agent:charles on behalf of user:dom", delegated.ToString());
        Assert.Equal(delegated, new PrincipalAction(Principal.Agent("charles"), Principal.User("dom")));
        Assert.NotEqual(delegated, new PrincipalAction(Principal.Agent("charles")));
    }

    [Theory]
    [InlineData("service:kek")]
    [InlineData("agent:charles on behalf of user:dom")]
    [InlineData("service:device-1 on behalf of user:dom")]
    public void Principal_action_round_trips(string text)
    {
        var action = PrincipalAction.Parse(text);

        Assert.Equal(text, action.ToString());
        Assert.Equal(action, PrincipalAction.Parse(action.ToString()));
    }

    [Fact]
    public void Principal_action_parse_exposes_both_principals()
    {
        var action = PrincipalAction.Parse("agent:charles on behalf of user:dom");

        Assert.Equal(Principal.Agent("charles"), action.Actor);
        Assert.Equal(Principal.User("dom"), action.OnBehalfOf);
        Assert.Null(PrincipalAction.Parse("agent:charles").OnBehalfOf);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("agent:charles on behalf of")]
    [InlineData("agent:charles on behalf of ")]
    [InlineData(" on behalf of user:dom")]
    [InlineData("agent:charles on behalf of nobody")]
    [InlineData("agent:charles on behalf of user:dom on behalf of user:x")]
    [InlineData("agent:charles  on behalf of user:dom")]
    [InlineData("agent:charles on behalf of user:dom ")]
    [InlineData("robot:x on behalf of user:dom")]
    public void Principal_action_rejects_invalid_text(string? text)
    {
        Assert.False(PrincipalAction.TryParse(text, out var action));
        Assert.Null(action);
        Assert.Throws<FormatException>(() => PrincipalAction.Parse(text!));
    }

    [Theory]
    [InlineData("install:main")]
    [InlineData("tenant:acme")]
    [InlineData("tenant:acme/branch/7")]
    [InlineData("user:dom")]
    public void Scope_round_trips(string text)
    {
        var s = Scope.Parse(text);

        Assert.Equal(text, s.ToString());
        Assert.Equal(s, Scope.Parse(s.ToString()));
    }

    [Fact]
    public void Scope_exposes_kind_id_and_path()
    {
        var s = Scope.Parse("tenant:acme/branch/7");

        Assert.Equal(ScopeKind.Tenant, s.Kind);
        Assert.Equal("acme", s.Id);
        Assert.Equal(["branch", "7"], s.Path);
        Assert.Equal(s, Scope.Tenant("acme", "branch", "7"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tenant")]
    [InlineData("tenant:")]
    [InlineData("Tenant:acme")]
    [InlineData("org:acme")]
    [InlineData("tenant:acme/")]
    [InlineData("tenant:/acme")]
    [InlineData("tenant:acme//7")]
    [InlineData("tenant:acme:x")]
    [InlineData("tenant:ac me")]
    [InlineData("install:main/sub")]
    [InlineData("user:dom/sub")]
    [InlineData("tenant:acme/\u0001")]
    public void Scope_rejects_invalid_text(string? text)
    {
        Assert.False(Scope.TryParse(text, out var s));
        Assert.Null(s);
    }

    [Fact]
    public void Scope_create_rejects_bad_input()
    {
        Assert.Throws<ArgumentException>(() => Scope.Install("a/b"));
        Assert.Throws<ArgumentException>(() => Scope.Create(ScopeKind.User, "dom", "sub"));
        Assert.Throws<ArgumentException>(() => Scope.Tenant("acme", ""));
        Assert.Throws<ArgumentException>(() => Scope.Tenant(new string('a', 129)));
        Assert.Throws<FormatException>(() => Scope.Parse("x"));
    }

    [Fact]
    public void Scope_path_is_copied_so_callers_cannot_mutate_it()
    {
        string[] path = ["branch"];
        var s = Scope.Tenant("acme", path);
        path[0] = "other";

        Assert.Equal("tenant:acme/branch", s.ToString());
    }

    [Fact]
    public void Scope_containment_is_narrower_inside_wider()
    {
        var tenant = Scope.Parse("tenant:acme");
        var branch = Scope.Parse("tenant:acme/branch");
        var seven = Scope.Parse("tenant:acme/branch/7");

        Assert.True(tenant.Contains(seven));
        Assert.True(branch.Contains(seven));
        Assert.True(seven.IsWithin(tenant));
        Assert.True(seven.Contains(seven));
        Assert.False(seven.Contains(tenant));
        Assert.False(tenant.IsWithin(seven));
        Assert.False(Scope.Parse("tenant:acme/branch/8").Contains(seven));
        Assert.False(Scope.Parse("tenant:acme/bran").Contains(branch));
        Assert.False(Scope.Parse("tenant:other").Contains(seven));
    }

    [Fact]
    public void Scopes_of_different_kinds_never_contain_each_other()
    {
        Assert.False(Scope.Install("main").Contains(Scope.Tenant("main")));
        Assert.False(Scope.Tenant("dom").Contains(Scope.User("dom")));
        Assert.False(Scope.User("dom").IsWithin(Scope.Install("dom")));
    }

    [Fact]
    public void Scope_equality_and_ordering()
    {
        Assert.Equal(Scope.Parse("tenant:acme/a"), Scope.Tenant("acme", "a"));
        Assert.Equal(Scope.Parse("tenant:acme/a").GetHashCode(), Scope.Tenant("acme", "a").GetHashCode());
        Assert.NotEqual(Scope.Parse("tenant:acme/a"), Scope.Parse("tenant:acme/b"));
        Assert.True(Scope.Install("x") != Scope.User("x"));

        var unsorted = new List<string> { "user:a", "tenant:b/z", "tenant:b", "install:m", "tenant:a", "tenant:b/a" };
        var sorted = unsorted
            .Select(Scope.Parse).Order().Select(s => s.ToString());
        Assert.Equal(["install:m", "tenant:a", "tenant:b", "tenant:b/a", "tenant:b/z", "user:a"], sorted);
    }

    [Fact]
    public void Cdax_core_project_has_no_package_or_project_references()
    {
        var csproj = Path.Combine(FindRepoRoot(), "src", "Cdax.Core", "Cdax.Core.csproj");
        var text = File.ReadAllText(csproj);

        Assert.DoesNotContain("PackageReference", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", text, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
