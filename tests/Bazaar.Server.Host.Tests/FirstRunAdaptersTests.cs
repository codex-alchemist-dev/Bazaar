using Bazaar.Contracts.Soul;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Host.Composition;

namespace Bazaar.Server.Host.Tests;

public class FirstRunAdaptersTests
{
    private sealed class Checker(bool allow) : IPermissionChecker
    {
        public (Guid Principal, PrincipalKind Kind, Capability Capability, Scope Scope)? Asked { get; private set; }

        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default)
        {
            Asked = (principalId, kind, capability, scope);
            return Task.FromResult(allow);
        }
    }

    [Fact]
    public async Task Access_asks_for_read_across_the_company_for_a_human()
    {
        var human = Guid.NewGuid();
        var checker = new Checker(true);

        Assert.True(await new PermissionProfileAccess(checker).CanReadAsync(human.ToString(), Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal((human, PrincipalKind.Human, Capabilities.Read, Scope.Company), checker.Asked);
    }

    [Fact]
    public async Task Access_is_false_for_a_refused_check_and_for_a_person_id_that_is_not_a_human_id_without_asking()
    {
        Assert.False(await new PermissionProfileAccess(new Checker(false)).CanReadAsync(Guid.NewGuid().ToString(), Guid.NewGuid(), TestContext.Current.CancellationToken));

        var checker = new Checker(true);
        Assert.False(await new PermissionProfileAccess(checker).CanReadAsync("not-a-guid", Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(checker.Asked);
    }

    [Fact]
    public void A_soul_version_maps_to_the_metadata_the_route_shows()
    {
        var at = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        var mapped = SoulVersioningVersionReader.Map(new SoulVersion(Guid.NewGuid(), 2, 1, "c", "{}", "h", "owner", "tweak", "d-1", at));

        Assert.Equal(2, mapped.Version);
        Assert.Equal(1, mapped.ParentVersion);
        Assert.Equal("d-1", mapped.DecisionRef);
        Assert.Equal(at, mapped.CreatedAt);
    }

    [Fact]
    public void The_host_ships_the_adapters_after_the_profile_component()
    {
        var ids = HostComponents.All.Select(c => c.Manifest.Id).ToList();
        Assert.Contains("first-run-adapters", ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }
}

public class BearerCallerResolverTests
{
    private sealed class Lookup(Guid? human) : IBearerPersonLookup
    {
        public string? Token { get; private set; }

        public Task<Guid?> FindHumanAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            Token = accessToken;
            return Task.FromResult(human);
        }
    }

    [Fact]
    public async Task A_bearer_token_for_a_live_human_resolves_to_the_human_id_text()
    {
        var human = Guid.NewGuid();
        var lookup = new Lookup(human);

        Assert.Equal(human.ToString(), await new BearerCallerResolver(lookup).ResolveAsync("Bearer abc.def", TestContext.Current.CancellationToken));
        Assert.Equal("abc.def", lookup.Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer two parts")]
    public async Task Anything_that_is_not_one_bearer_token_resolves_to_null_without_a_lookup(string? header)
    {
        var lookup = new Lookup(Guid.NewGuid());

        Assert.Null(await new BearerCallerResolver(lookup).ResolveAsync(header, TestContext.Current.CancellationToken));
        Assert.Null(lookup.Token);
    }

    [Fact]
    public async Task A_refused_token_and_the_default_lookup_resolve_to_null()
    {
        Assert.Null(await new BearerCallerResolver(new Lookup(null)).ResolveAsync("Bearer abc", TestContext.Current.CancellationToken));
        Assert.Null(await new BearerCallerResolver(new UnavailableBearerPersonLookup()).ResolveAsync("Bearer abc", TestContext.Current.CancellationToken));
    }
}
