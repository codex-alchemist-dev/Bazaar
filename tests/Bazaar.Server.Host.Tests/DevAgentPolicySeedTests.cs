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
}
