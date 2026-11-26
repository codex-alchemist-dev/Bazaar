using Bazaar.Contracts.Engines;

namespace Bazaar.Engines.Grants;

/// <summary>
/// Default-deny rule for adapter tools (docs/design/20-engine-integration-api.md, Data and interfaces). <c>read</c> tools
/// run without a grant; <c>write</c> and <c>exec</c> tools are denied unless a matching grant row exists.
/// </summary>
public static class EngineGrantPolicy
{
    /// <summary>True when a tool of this risk class may run only with a grant row.</summary>
    public static bool RequiresGrant(RiskClass riskClass) => riskClass switch
    {
        RiskClass.Read => false,
        RiskClass.Write or RiskClass.Exec => true,
        _ => throw new ArgumentOutOfRangeException(nameof(riskClass), riskClass, "Unknown risk class."),
    };
}
