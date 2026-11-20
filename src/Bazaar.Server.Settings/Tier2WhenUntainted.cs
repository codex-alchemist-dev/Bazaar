// Lives with the settings module that owns the key; the namespace stays Bazaar.Server.Security.Policy so the policy engine and its tests are unchanged.
namespace Bazaar.Server.Security.Policy;

/// <summary>The setting <c>security.approval.tier2_when_untainted</c>.</summary>
public enum Tier2WhenUntainted
{
    /// <summary>Every Tier 2 call needs approval, whatever the session's taint.</summary>
    Always,

    /// <summary>Tier 2 needs approval only when the session is hard-tainted.</summary>
    HardTaintOnly,
}
