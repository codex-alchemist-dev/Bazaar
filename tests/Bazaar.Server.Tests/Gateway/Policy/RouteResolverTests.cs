using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Tests.Gateway.Policy;

public class RouteResolverTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private static InMemoryPolicyWorld World() => new InMemoryPolicyWorld()
        .AddProvider("anthropic-direct").AddProvider("freellmapi").AddProvider("openai")
        .AddModel("anthropic-direct", "claude-haiku-5-5").AddModel("freellmapi", "claude-haiku-5-5")
        .AddModel("anthropic-direct", "claude-opus-5-5").AddModel("openai", "gpt-x", "gpt");

    private static RouteResolution.Allowed AssertAllowed(RouteResolution r) => Assert.IsType<RouteResolution.Allowed>(r);

    private static RouteResolution.Denied AssertDenied(RouteResolution r, DenyReason reason)
    {
        var denied = Assert.IsType<RouteResolution.Denied>(r);
        Assert.Equal(reason, denied.Reason);
        return denied;
    }

    [Fact]
    public async Task Unknown_agent_is_denied()
    {
        var world = World().AddPolicy("a1", ["anthropic-direct:claude-haiku-5-5"]);
        var r = await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("ghost", "anthropic-direct:claude-haiku-5-5"), _ct);
        AssertDenied(r, DenyReason.UnknownAgent);
    }

    [Fact]
    public async Task Model_not_on_the_whitelist_is_denied()
    {
        var world = World().AddPolicy("a1", ["anthropic-direct:claude-haiku-5-5"]);
        var r = await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "anthropic-direct:claude-opus-5-5"), _ct);
        AssertDenied(r, DenyReason.ModelNotAllowed);
    }

    [Fact]
    public async Task Empty_policy_denies_everything()
    {
        var world = World().AddPolicy("a1", []);
        var r = await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "anthropic-direct:claude-haiku-5-5"), _ct);
        AssertDenied(r, DenyReason.ModelNotAllowed);
    }

    [Fact]
    public async Task Listed_model_resolves_to_a_single_candidate()
    {
        var world = World().AddPolicy("a1", ["anthropic-direct:claude-haiku-5-5"]);
        var r = AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "anthropic-direct:claude-haiku-5-5"), _ct));
        Assert.Equal(new ModelRef("anthropic-direct", "claude-haiku-5-5"), Assert.Single(r.Route.Candidates).Model);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("auto/cheap")]
    [InlineData("AUTO/cheap")]
    [InlineData("*")]
    [InlineData("custom/*")]
    [InlineData("anthropic-direct:*")]
    [InlineData("anthropic-direct:auto/best")]
    public async Task Wildcard_and_auto_routes_never_resolve(string route)
    {
        var world = World().AddPolicy("a1", ["anthropic-direct:claude-haiku-5-5"]);
        var r = await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", route), _ct);
        Assert.IsType<RouteResolution.Denied>(r);
    }

    [Theory]
    [InlineData("auto/cheap")]
    [InlineData("*")]
    [InlineData("anthropic-direct:*")]
    [InlineData("anthropic-direct:auto/x")]
    [InlineData("")]
    [InlineData("a b:c")]
    public void Policy_rejects_wildcard_auto_and_unqualified_entries(string entry)
    {
        Assert.Throws<PolicyFormatException>(() => AgentModelPolicy.Create("a1", [entry], null, 1));
    }

    [Fact]
    public async Task A_bare_model_name_is_only_a_route_name_and_never_a_model_grant()
    {
        var world = World().AddPolicy("a1", ["claude-haiku-5-5"]);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "claude-haiku-5-5"), _ct), DenyReason.UnknownRoute);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "anthropic-direct:claude-haiku-5-5"), _ct), DenyReason.ModelNotAllowed);
    }

    [Fact]
    public void Policy_rejects_duplicates_and_a_default_that_is_not_allowed()
    {
        Assert.Throws<PolicyFormatException>(() => AgentModelPolicy.Create("a1", ["p:m", "p:m"], null, 1));
        Assert.Throws<PolicyFormatException>(() => AgentModelPolicy.Create("a1", ["p:m"], "p:other", 1));
        Assert.Throws<PolicyFormatException>(() => AgentModelPolicy.Create("a1", ["p:m"], "some-route", 1));
    }

    [Fact]
    public async Task Default_route_is_used_when_the_request_names_none()
    {
        var world = World().AddPolicy("a1", ["anthropic-direct:claude-haiku-5-5"], "anthropic-direct:claude-haiku-5-5");
        AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", ""), _ct));

        world.AddPolicy("a2", ["anthropic-direct:claude-haiku-5-5"]);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a2", ""), _ct), DenyReason.NoRoute);
    }

    [Fact]
    public async Task Route_needs_to_be_listed_and_its_members_listed_as_models()
    {
        var world = World()
            .AddRoute("claude-lane", ["anthropic-direct:claude-haiku-5-5", "anthropic-direct:claude-opus-5-5"])
            .AddPolicy("a1", ["claude-lane", "anthropic-direct:claude-haiku-5-5"])
            .AddPolicy("a2", ["anthropic-direct:claude-haiku-5-5"])
            .AddPolicy("a3", ["claude-lane"]);

        var allowed = AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "claude-lane"), _ct));
        Assert.Equal([new ModelRef("anthropic-direct", "claude-haiku-5-5")], allowed.Route.Candidates.Select(c => c.Model));

        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a2", "claude-lane"), _ct), DenyReason.RouteNotAllowed);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a3", "claude-lane"), _ct), DenyReason.NoEligibleCandidate);
    }

    [Fact]
    public async Task Adding_a_model_to_a_shared_route_does_not_widen_an_agent()
    {
        var world = World()
            .AddRoute("lane", ["anthropic-direct:claude-haiku-5-5"])
            .AddPolicy("a1", ["lane", "anthropic-direct:claude-haiku-5-5"]);
        world.Routes["lane"] = world.Routes["lane"] with { Members = [new ModelRef("anthropic-direct", "claude-haiku-5-5"), new ModelRef("anthropic-direct", "claude-opus-5-5")] };

        var r = AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "lane"), _ct));
        Assert.DoesNotContain(r.Route.Candidates, c => c.Model.ModelId == "claude-opus-5-5");
    }

    [Fact]
    public async Task Forced_fallback_never_reaches_an_excluded_model()
    {
        var world = World()
            .AddRoute("lane", ["anthropic-direct:claude-haiku-5-5", "anthropic-direct:claude-opus-5-5", "freellmapi:claude-haiku-5-5"])
            .AddPolicy("a1", ["lane", "anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5"]);
        var r = AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "lane"), _ct)).Route;

        var tried = new HashSet<ModelRef>();
        var visited = new List<ModelRef>();
        while (r.Next(tried) is { } next)
        {
            visited.Add(next.Model);
            tried.Add(next.Model);
        }

        Assert.Equal([new ModelRef("anthropic-direct", "claude-haiku-5-5"), new ModelRef("freellmapi", "claude-haiku-5-5")], visited);
        Assert.False(r.Authorizes(new ModelRef("anthropic-direct", "claude-opus-5-5")));
        Assert.Null(r.Next(tried));
    }

    [Fact]
    public async Task Next_skips_explicitly_excluded_models()
    {
        var world = World()
            .AddRoute("lane", ["anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5"])
            .AddPolicy("a1", ["lane", "anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5"]);
        var r = AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "lane"), _ct)).Route;
        var next = r.Next(new HashSet<ModelRef>(), new HashSet<ModelRef> { new("anthropic-direct", "claude-haiku-5-5") });
        Assert.Equal(new ModelRef("freellmapi", "claude-haiku-5-5"), next?.Model);
    }

    [Fact]
    public async Task Fallback_does_not_cross_model_families()
    {
        var world = World()
            .AddRoute("mixed", ["anthropic-direct:claude-haiku-5-5", "openai:gpt-x"])
            .AddPolicy("a1", ["mixed", "anthropic-direct:claude-haiku-5-5", "openai:gpt-x"]);
        var allowed = AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "mixed"), _ct));
        Assert.Single(allowed.Route.Candidates);
        Assert.Contains(allowed.Dropped, d => d.Reason == DenyReason.FamilyMismatch);
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("oauth")]
    [InlineData("claude_code")]
    [InlineData("session_cookie")]
    [InlineData("")]
    public async Task Subscription_or_unknown_credential_kinds_are_refused(string authKind)
    {
        var world = World().AddProvider("sub", authKind).AddModel("sub", "claude-haiku-5-5")
            .AddPolicy("a1", ["sub:claude-haiku-5-5"]);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "sub:claude-haiku-5-5"), _ct), DenyReason.CredentialRefused);
    }

    [Theory]
    [InlineData("sk-ant-api03-literal-key")]
    [InlineData("secret://other/path")]
    [InlineData(null)]
    public async Task A_literal_key_or_foreign_secret_reference_is_refused(string? secretRef)
    {
        var world = World().AddProvider("bad", "api_key", secretRef: secretRef).AddModel("bad", "m")
            .AddPolicy("a1", ["bad:m"]);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "bad:m"), _ct), DenyReason.CredentialRefused);
    }

    [Fact]
    public async Task Auth_none_is_accepted_without_a_secret_but_not_with_one()
    {
        var world = World().AddProvider("local", "none").AddModel("local", "llama").AddPolicy("a1", ["local:llama"]);
        AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "local:llama"), _ct));

        world.Providers["local"] = world.Providers["local"] with { SecretRef = "secret://bazaar/providers/local/k" };
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "local:llama"), _ct), DenyReason.CredentialRefused);
    }

    [Fact]
    public async Task Disabled_provider_and_missing_model_are_dropped()
    {
        var world = World().AddProvider("off", enabled: false).AddModel("off", "m")
            .AddModel("anthropic-direct", "gone", status: GwModel.StatusMissing)
            .AddPolicy("a1", ["off:m", "anthropic-direct:gone", "anthropic-direct:unlisted-in-catalogue"]);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "off:m"), _ct), DenyReason.ProviderUnavailable);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "anthropic-direct:gone"), _ct), DenyReason.ModelUnavailable);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "anthropic-direct:unlisted-in-catalogue"), _ct), DenyReason.ModelUnavailable);
    }

    [Fact]
    public async Task Tightening_only_removes_access()
    {
        var world = World().AddPolicy("a1", ["anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5"])
            .AddRoute("lane", ["anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5"]);
        world.AddPolicy("a2", ["lane", "anthropic-direct:claude-haiku-5-5", "freellmapi:claude-haiku-5-5"]);

        var tight = new PolicyTightening(["freellmapi"], []);
        AssertDenied(await world.Resolver(tight).ResolveAsync(InMemoryPolicyWorld.Request("a1", "freellmapi:claude-haiku-5-5"), _ct), DenyReason.DeniedByTightening);
        var lane = AssertAllowed(await world.Resolver(tight).ResolveAsync(InMemoryPolicyWorld.Request("a2", "lane"), _ct));
        Assert.Equal("anthropic-direct", Assert.Single(lane.Route.Candidates).Model.ProviderId);

        // Tightening cannot grant: a model absent from the policy stays denied whatever it says.
        var other = new PolicyTightening([], [new ModelRef("x", "y")]).And(PolicyTightening.None);
        AssertDenied(await world.Resolver(other).ResolveAsync(InMemoryPolicyWorld.Request("a1", "anthropic-direct:claude-opus-5-5"), _ct), DenyReason.ModelNotAllowed);
        Assert.True(tight.And(other).Denies(new ModelRef("x", "y")));
        Assert.True(tight.And(other).Denies(new ModelRef("freellmapi", "anything")));
    }

    [Fact]
    public async Task Gatekeeper_subject_may_not_send_tools_but_may_call_its_list()
    {
        var world = World().AddPolicy("gatekeeper", ["anthropic-direct:claude-haiku-5-5"]);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("gatekeeper", "anthropic-direct:claude-haiku-5-5", tools: true), _ct), DenyReason.ToolsNotAllowed);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("gatekeeper", "anthropic-direct:claude-opus-5-5"), _ct), DenyReason.ModelNotAllowed);
        AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("gatekeeper", "anthropic-direct:claude-haiku-5-5"), _ct));
    }

    [Fact]
    public async Task Delegated_route_needs_an_expected_set_inside_the_policy()
    {
        var world = World()
            .AddRoute("custom/cheap", ["freellmapi:profile-cheap"], ["claude-haiku-5-5"])
            .AddRoute("custom/wide", ["freellmapi:profile-wide"], ["claude-haiku-5-5", "claude-opus-5-5"])
            .AddRoute("custom/empty", ["freellmapi:profile-x"])
            .AddPolicy("a1", ["custom/cheap", "custom/wide", "custom/empty", "freellmapi:claude-haiku-5-5"]);

        var ok = AssertAllowed(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "custom/cheap"), _ct));
        Assert.True(ok.Route.IsDelegated);
        Assert.Equal(["claude-haiku-5-5"], ok.Route.ExpectedSet);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "custom/wide"), _ct), DenyReason.DelegatedSetExceedsPolicy);
        AssertDenied(await world.Resolver().ResolveAsync(InMemoryPolicyWorld.Request("a1", "custom/empty"), _ct), DenyReason.DelegatedSetExceedsPolicy);
    }

    [Fact]
    public void Delegated_audit_flags_a_served_model_outside_the_expected_set()
    {
        var route = new GwRoute("custom/cheap", RouteStrategy.Priority, [], ["claude-haiku-5-5", "freellmapi:gpt-x"]);
        Assert.Empty(DelegatedRouteAudit.Violations(route, "claude-haiku-5-5", ["gpt-x"]));
        Assert.Equal(["claude-opus-5-5"], DelegatedRouteAudit.Violations(route, "claude-opus-5-5"));
        Assert.Equal(["rogue"], DelegatedRouteAudit.Violations(route, "claude-haiku-5-5", ["rogue"]));
        Assert.NotEmpty(DelegatedRouteAudit.Violations(route, null));
    }

    [Fact]
    public void Credential_kinds_are_exactly_three()
    {
        Assert.Equal(3, Enum.GetValues<CredentialKind>().Length);
        Assert.True(CredentialKinds.TryParse("api_key", out _));
        Assert.True(CredentialKinds.TryParse("bearer", out _));
        Assert.True(CredentialKinds.TryParse("none", out _));
        Assert.False(CredentialKinds.TryParse("oauth", out _));
    }
}
