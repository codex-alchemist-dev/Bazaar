using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Host.Composition;

namespace Bazaar.Server.Host.Tests;

public sealed class DevAgentPolicySeedTests
{
    [Fact]
    public void EntriesGrantTheDefaultRouteAndItsProviderQualifiedModel()
    {
        var options = new FreeLlmApiOptions { Enabled = true, DefaultModel = "some-model", RouteName = "agent-default" };

        var entries = DevAgentPolicySeed.EntriesFor(options);

        Assert.Equal(["agent-default", "freellmapi:some-model"], entries);
    }

    [Fact]
    public void ChangedByLabelMarksTheRowAsSeeded()
    {
        Assert.Equal("dev-seed", DevAgentPolicySeed.ChangedBy);
    }

    [Fact]
    public void NothingIsSeededWhenBothSourcesAreOff()
    {
        Assert.Null(DevAgentPolicySeed.PolicyFor(new FreeLlmApiOptions(), new AnthropicDirectOptions()));
        Assert.Empty(DevAgentPolicySeed.EntriesFor(new AnthropicDirectOptions()));
    }

    [Fact]
    public void AnthropicAloneGrantsOnlyTheHaikuRouteAndModelAndMakesItTheDefault()
    {
        var anthropic = new AnthropicDirectOptions { Enabled = true, Models = ["claude-haiku-5-5", "claude-sonnet-5-5"] };

        var policy = DevAgentPolicySeed.PolicyFor(new FreeLlmApiOptions(), anthropic);

        Assert.NotNull(policy);
        Assert.Equal(["claude-haiku-5-5", "anthropic-direct:claude-haiku-5-5"], policy.Value.Entries);
        Assert.Equal("claude-haiku-5-5", policy.Value.DefaultRoute);
    }

    [Fact]
    public void AnthropicWithoutTheHaikuModelGrantsNothing()
    {
        var anthropic = new AnthropicDirectOptions { Enabled = true, Models = ["claude-sonnet-5-5"] };

        Assert.Null(DevAgentPolicySeed.PolicyFor(new FreeLlmApiOptions(), anthropic));
    }

    [Fact]
    public void BothSourcesAreGrantedTogetherAndFreeLlmApiKeepsTheDefaultRoute()
    {
        var free = new FreeLlmApiOptions { Enabled = true, DefaultModel = "some-model", RouteName = "agent-default" };

        var policy = DevAgentPolicySeed.PolicyFor(free, new AnthropicDirectOptions { Enabled = true });

        Assert.NotNull(policy);
        Assert.Equal(
            ["agent-default", "freellmapi:some-model", "claude-haiku-5-5", "anthropic-direct:claude-haiku-5-5"],
            policy.Value.Entries);
        Assert.Equal("agent-default", policy.Value.DefaultRoute);
    }

    [Fact]
    public void ARouteNameSharedByBothSourcesIsWrittenOnce()
    {
        var free = new FreeLlmApiOptions { Enabled = true, DefaultModel = "some-model", RouteName = "claude-haiku-5-5" };

        var policy = DevAgentPolicySeed.PolicyFor(free, new AnthropicDirectOptions { Enabled = true });

        Assert.NotNull(policy);
        Assert.Single(policy.Value.Entries, e => e == "claude-haiku-5-5");
    }
}
