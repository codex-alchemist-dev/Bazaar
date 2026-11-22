using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Providers.ClaudeCode;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Host.Tests;

public sealed class ClaudeCodeGatewayTests
{
    private const string AgentId = "0d3a1f0e-1111-4222-8333-444455556666";

    private static readonly ClaudeCodeOptions _cli = new() { Enabled = true, LoginOwner = "user:owner", TermsAcknowledgedAt = DateTimeOffset.UnixEpoch };

    private sealed class EmptyDirectory : IGatewayDirectory
    {
        public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) => ValueTask.FromResult<GwProvider?>(null);

        public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) => ValueTask.FromResult<GwModel?>(null);

        public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) => ValueTask.FromResult<GwRoute?>(null);
    }

    private sealed class FixedPolicy(AgentModelPolicy policy) : IAgentPolicyStore
    {
        public ValueTask<AgentModelPolicy?> GetAsync(string agentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentModelPolicy?>(agentId == policy.AgentId ? policy : null);
    }

    /// <summary>Built the way the host composes it: the Anthropic overlay is the inner directory, the CLI overlay goes on top.</summary>
    private static ClaudeCodeGatewayDirectory Directory(AnthropicDirectOptions? anthropic = null)
    {
        var time = new FakeTimeProvider();
        IGatewayDirectory inner = anthropic is { Enabled: true } ? new AnthropicDirectGatewayDirectory(new EmptyDirectory(), anthropic, time) : new EmptyDirectory();
        return new ClaudeCodeGatewayDirectory(inner, _cli, anthropic, time);
    }

    [Fact]
    public async Task The_provider_row_has_no_credential_and_the_sentinel_url()
    {
        var provider = await Directory().GetProviderAsync(ClaudeCodeProvider.ProviderId, TestContext.Current.CancellationToken);

        Assert.NotNull(provider);
        Assert.Equal("claude_code_cli", provider.Adapter);
        Assert.Equal("cli://claude-code", provider.BaseUrl);
        Assert.Equal("none", provider.AuthKind);
        Assert.Null(provider.SecretRef);
        Assert.True(CredentialKinds.IsAcceptable(provider.AuthKind, provider.SecretRef));
        Assert.Equal(_cli.TermsAcknowledgedAt, provider.TermsAckAt);
    }

    [Fact]
    public async Task Models_carry_the_subscription_identity_and_the_claude_family()
    {
        var model = await Directory().GetModelAsync(new ModelRef(ClaudeCodeProvider.ProviderId, ClaudeCodeOptions.DefaultModel), TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        Assert.Equal(GatewayIdentity.Subscription, model.Identity);
        Assert.Equal("claude", model.Family);
        Assert.Equal(GwModel.StatusAvailable, model.Status);
    }

    [Fact]
    public async Task Anything_else_goes_to_the_inner_directory()
    {
        Assert.Null(await Directory().GetProviderAsync("freellmapi", TestContext.Current.CancellationToken));
        Assert.Null(await Directory().GetRouteAsync("agent-default", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_default_route_falls_back_to_the_owners_key_only_inside_the_claude_family()
    {
        var withKey = await Directory(new AnthropicDirectOptions { Enabled = true }).GetRouteAsync(ClaudeCodeProvider.DefaultRoute, TestContext.Current.CancellationToken);
        var alone = await Directory().GetRouteAsync(ClaudeCodeProvider.DefaultRoute, TestContext.Current.CancellationToken);

        Assert.NotNull(withKey);
        Assert.Equal(
            [new ModelRef("claude-code-cli", "claude-haiku-5-5"), new ModelRef("anthropic-direct", "claude-haiku-5-5")],
            withKey.Members);
        Assert.NotNull(alone);
        Assert.Equal([new ModelRef("claude-code-cli", "claude-haiku-5-5")], alone.Members);
    }

    [Fact]
    public async Task The_claude_route_lists_every_claude_source_and_adds_stored_members()
    {
        var anthropic = new AnthropicDirectOptions { Enabled = true, Models = ["claude-haiku-5-5", "claude-sonnet-5-5"] };
        var cli = new ClaudeCodeOptions { Enabled = true, Models = ["claude-haiku-5-5", "claude-opus-5-5"] };
        var time = new FakeTimeProvider();
        var inner = new AnthropicDirectGatewayDirectory(new StoredRouteDirectory(new GwRoute("claude", RouteStrategy.Priority, [new ModelRef("my-proxy", "claude-x")], [])), anthropic, time);
        var directory = new ClaudeCodeGatewayDirectory(inner, cli, anthropic, time);

        var route = await directory.GetRouteAsync("claude", TestContext.Current.CancellationToken);

        Assert.NotNull(route);
        Assert.Equal(
            [
                new ModelRef("claude-code-cli", "claude-haiku-5-5"),
                new ModelRef("claude-code-cli", "claude-opus-5-5"),
                new ModelRef("anthropic-direct", "claude-haiku-5-5"),
                new ModelRef("anthropic-direct", "claude-sonnet-5-5"),
                new ModelRef("my-proxy", "claude-x"),
            ],
            route.Members);
    }

    private sealed class StoredRouteDirectory(GwRoute route) : IGatewayDirectory
    {
        public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) => ValueTask.FromResult<GwProvider?>(null);

        public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) => ValueTask.FromResult<GwModel?>(null);

        public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<GwRoute?>(routeId == route.Id ? route : null);
    }

    [Fact]
    public void The_seeded_policy_makes_the_cli_route_the_default_and_keeps_freellmapi_as_a_second_route()
    {
        var free = new FreeLlmApiOptions { Enabled = true, DefaultModel = "some-model", RouteName = "agent-default" };

        var policy = DevAgentPolicySeed.PolicyFor(free, new AnthropicDirectOptions { Enabled = true }, _cli);

        Assert.NotNull(policy);
        Assert.Equal("claude", policy.Value.DefaultRoute);
        Assert.Equal(
            [
                "claude",
                "claude/claude-haiku-5-5",
                "claude-code-cli:claude-haiku-5-5",
                "agent-default",
                "freellmapi:some-model",
                "claude-haiku-5-5",
                "anthropic-direct:claude-haiku-5-5",
            ],
            policy.Value.Entries);
        AgentModelPolicy.Create(AgentId, policy.Value.Entries, policy.Value.DefaultRoute, 1);
    }

    [Fact]
    public void The_cli_alone_seeds_a_policy_and_a_disabled_cli_seeds_nothing()
    {
        var policy = DevAgentPolicySeed.PolicyFor(new FreeLlmApiOptions(), new AnthropicDirectOptions(), _cli);

        Assert.NotNull(policy);
        Assert.Equal("claude", policy.Value.DefaultRoute);
        Assert.Null(DevAgentPolicySeed.PolicyFor(new FreeLlmApiOptions(), new AnthropicDirectOptions(), new ClaudeCodeOptions()));
    }

    [Fact]
    public async Task A_seeded_agent_resolves_to_the_cli_first_and_the_owners_key_second()
    {
        var seeded = DevAgentPolicySeed.PolicyFor(new FreeLlmApiOptions(), new AnthropicDirectOptions { Enabled = true }, _cli)!.Value;
        var policy = AgentModelPolicy.Create(AgentId, seeded.Entries, seeded.DefaultRoute, 1);
        var resolver = new RouteResolver(new FixedPolicy(policy), Directory(new AnthropicDirectOptions { Enabled = true }));

        var resolution = await resolver.ResolveAsync(new ModelRequest(AgentId, "", [new ModelMessage(ModelRole.User, "hi")], []), TestContext.Current.CancellationToken);

        var allowed = Assert.IsType<RouteResolution.Allowed>(resolution);
        Assert.Equal("claude", allowed.Route.Route);
        Assert.Equal(["claude-code-cli", "anthropic-direct"], allowed.Route.Candidates.Select(c => c.Model.ProviderId));
    }

    [Fact]
    public async Task An_agent_without_the_seed_is_still_denied()
    {
        var policy = AgentModelPolicy.Create(AgentId, ["agent-default", "freellmapi:some-model"], "agent-default", 1);
        var resolver = new RouteResolver(new FixedPolicy(policy), Directory());

        var resolution = await resolver.ResolveAsync(new ModelRequest(AgentId, "claude", [new ModelMessage(ModelRole.User, "hi")], []), TestContext.Current.CancellationToken);

        Assert.IsType<RouteResolution.Denied>(resolution);
    }
}
