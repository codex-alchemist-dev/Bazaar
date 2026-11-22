using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Setup.FirstRun.Gateway.Tests;

internal sealed class FakeDirectory(params ClaudeSourceModel[] models) : IClaudeSourceDirectory, IDefaultAgentPolicyStore
{
    public Dictionary<string, IReadOnlyList<ModelRef>> Routes { get; } = [];
    public Dictionary<string, (IReadOnlyList<string> Entries, string? Default, string ChangedBy)> Policies { get; } = [];

    public Task<IReadOnlyList<ClaudeSourceModel>> ListClaudeModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClaudeSourceModel>>(models);

    public Task UpsertPriorityRouteAsync(string routeId, IReadOnlyList<ModelRef> members, CancellationToken cancellationToken = default)
    {
        Routes[routeId] = members;
        return Task.CompletedTask;
    }

    public Task<string?> GetChangedByAsync(string agentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Policies.TryGetValue(agentId, out var p) ? p.ChangedBy : null);

    public Task SaveAsync(string agentId, IReadOnlyList<string> entries, string? defaultRoute, string changedBy, CancellationToken cancellationToken = default)
    {
        Policies[agentId] = (entries, defaultRoute, changedBy);
        return Task.CompletedTask;
    }
}

public class GatewayDefaultAgentRoutesTests
{
    private static ClaudeSourceModel M(string provider, string model, bool native = false) => new(new ModelRef(provider, model), native);

    [Fact]
    public async Task Whitelist_is_route_plus_qualified_models_native_first()
    {
        var dir = new FakeDirectory(M("freellmapi", "claude-sonnet-5-5"), M("anthropic-direct", "claude-haiku-5-5", native: true));
        var list = await new GatewayDefaultAgentRoutes(dir).GetWhitelistAsync([], "balanced", TestContext.Current.CancellationToken);

        Assert.Equal(["default-agent-balanced", "anthropic-direct:claude-haiku-5-5", "freellmapi:claude-sonnet-5-5"], list);
        Assert.Equal(["anthropic-direct:claude-haiku-5-5", "freellmapi:claude-sonnet-5-5"], dir.Routes["default-agent-balanced"].Select(m => m.ToString()));
    }

    [Fact]
    public async Task Requested_sources_narrow_and_unknown_sources_are_ignored()
    {
        var dir = new FakeDirectory(M("freellmapi", "claude-sonnet-5-5"), M("anthropic-direct", "claude-haiku-5-5", native: true));
        var list = await new GatewayDefaultAgentRoutes(dir).GetWhitelistAsync(["FreeLLMAPI", "ghost"], "balanced", TestContext.Current.CancellationToken);

        Assert.Equal(["default-agent-balanced", "freellmapi:claude-sonnet-5-5"], list);
    }

    [Fact]
    public async Task No_claude_source_gives_empty_whitelist_and_writes_no_route()
    {
        var dir = new FakeDirectory();
        var list = await new GatewayDefaultAgentRoutes(dir).GetWhitelistAsync([], "balanced", TestContext.Current.CancellationToken);

        Assert.Empty(list);
        Assert.Empty(dir.Routes);
    }

    [Fact]
    public async Task Only_unknown_requested_sources_give_empty_whitelist()
    {
        var dir = new FakeDirectory(M("freellmapi", "claude-sonnet-5-5"));
        Assert.Empty(await new GatewayDefaultAgentRoutes(dir).GetWhitelistAsync(["ghost"], "balanced", TestContext.Current.CancellationToken));
        Assert.Empty(dir.Routes);
    }

    [Theory]
    [InlineData("balanced", "default-agent-balanced")]
    [InlineData("Fast & Cheap!", "default-agent-fast-cheap")]
    [InlineData("", "default-agent")]
    [InlineData("auto/x", "default-agent-auto-x")]
    public void Route_id_is_always_a_valid_route_name(string profile, string expected)
    {
        var id = GatewayDefaultAgentRoutes.RouteIdFor(profile);
        Assert.Equal(expected, id);
        Assert.True(RouteNames.IsValid(id));
    }

    [Fact]
    public async Task Duplicate_models_are_listed_once()
    {
        var dir = new FakeDirectory(M("a", "claude-x"), M("a", "claude-x"));
        var list = await new GatewayDefaultAgentRoutes(dir).GetWhitelistAsync([], "p", TestContext.Current.CancellationToken);
        Assert.Equal(["default-agent-p", "a:claude-x"], list);
    }
}

public class DefaultAgentPolicyGrantTests
{
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly string[] Whitelist = ["default-agent-balanced", "a:claude-x"];

    [Fact]
    public async Task Writes_whitelist_with_route_as_default_and_seed_label()
    {
        var dir = new FakeDirectory();
        Assert.True(await new DefaultAgentPolicyGrant(dir).GrantAsync(Agent, Whitelist, TestContext.Current.CancellationToken));

        var row = dir.Policies[Agent.ToString("D")];
        Assert.Equal(Whitelist, row.Entries);
        Assert.Equal("default-agent-balanced", row.Default);
        Assert.Equal(DefaultAgentPolicyGrant.ChangedBy, row.ChangedBy);
    }

    [Fact]
    public async Task Empty_whitelist_writes_nothing()
    {
        var dir = new FakeDirectory();
        Assert.False(await new DefaultAgentPolicyGrant(dir).GrantAsync(Agent, [], TestContext.Current.CancellationToken));
        Assert.Empty(dir.Policies);
    }

    [Fact]
    public async Task Operator_edited_row_is_never_overwritten()
    {
        var dir = new FakeDirectory();
        dir.Policies[Agent.ToString("D")] = (["x:y"], "x:y", "owner");

        Assert.False(await new DefaultAgentPolicyGrant(dir).GrantAsync(Agent, Whitelist, TestContext.Current.CancellationToken));
        Assert.Equal(["x:y"], dir.Policies[Agent.ToString("D")].Entries);
    }

    [Fact]
    public async Task Own_earlier_row_is_refreshed()
    {
        var dir = new FakeDirectory();
        dir.Policies[Agent.ToString("D")] = (["old:m"], "old:m", DefaultAgentPolicyGrant.ChangedBy);

        Assert.True(await new DefaultAgentPolicyGrant(dir).GrantAsync(Agent, Whitelist, TestContext.Current.CancellationToken));
        Assert.Equal(Whitelist, dir.Policies[Agent.ToString("D")].Entries);
    }
}
