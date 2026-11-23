using Bazaar.Server.Host.Display;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public class ScreenToolPolicyTests
{
    private static ToolCapability[] Registered(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<ToolCapability>()];
    }

    [Fact]
    public void Capture_is_a_tier_0_read_for_reader_and_actor_sessions_and_always_hard_taints()
    {
        var capture = Assert.Single(Registered(new ServiceCollection().AddBazaarScreenToolPolicy()), c => c.Name == "screen.capture");

        Assert.Equal(ToolTier.Autonomous, capture.Tier);
        Assert.Equal(CapabilityClass.Read, capture.Class);
        Assert.Equal(SessionRoles.Reader | SessionRoles.Actor, capture.Roles);
        Assert.Equal(ToolResultTaint.Hard, capture.ResultTaint);
    }

    [Fact]
    public void Input_is_a_tier_1_action_for_actor_sessions_only()
    {
        var input = Assert.Single(Registered(new ServiceCollection().AddBazaarScreenToolPolicy()), c => c.Name == "screen.input");

        Assert.Equal(ToolTier.AutonomousWithNotice, input.Tier);
        Assert.Equal(CapabilityClass.Exec, input.Class);
        Assert.Equal(SessionRoles.Actor, input.Roles);
        Assert.Equal(ToolResultTaint.None, input.ResultTaint);
    }

    [Fact]
    public void Registering_twice_adds_each_capability_once()
    {
        var all = Registered(new ServiceCollection().AddBazaarScreenToolPolicy().AddBazaarScreenToolPolicy());

        Assert.Equal(2, all.Length);
    }

    [Fact]
    public void Names_match_the_agent_tools()
    {
        Assert.Equal(Bazaar.Server.VmDisplay.AgentTools.ScreenCaptureTool.ToolName, ScreenToolPolicyExtensions.CaptureToolName);
        Assert.Equal(Bazaar.Server.VmDisplay.AgentTools.ScreenInputTool.ToolName, ScreenToolPolicyExtensions.InputToolName);
    }
}
