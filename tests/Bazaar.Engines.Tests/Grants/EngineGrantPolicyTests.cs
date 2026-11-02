using Bazaar.Contracts.Engines;
using Bazaar.Engines.Grants;

namespace Bazaar.Engines.Tests.Grants;

public class EngineGrantPolicyTests
{
    [Theory]
    [InlineData(RiskClass.Write)]
    [InlineData(RiskClass.Exec)]
    public void Write_and_exec_tools_are_default_deny(RiskClass riskClass)
    {
        Assert.True(EngineGrantPolicy.RequiresGrant(riskClass));
    }

    [Fact]
    public void Read_tools_do_not_need_a_grant()
    {
        Assert.False(EngineGrantPolicy.RequiresGrant(RiskClass.Read));
    }

    [Fact]
    public void Unknown_risk_class_is_rejected_rather_than_allowed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineGrantPolicy.RequiresGrant((RiskClass)99));
    }
}
