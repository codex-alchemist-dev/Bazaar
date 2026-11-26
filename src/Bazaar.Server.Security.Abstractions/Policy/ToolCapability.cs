namespace Bazaar.Server.Security.Policy;

/// <summary>
/// One registered tool: its tier, its capability class, the session roles it may run in, and an optional rate rule
/// (doc 15, "Enforcement" and "Shared quotas and limits").
/// </summary>
/// <param name="Name">Stable tool name the model calls. Matching is exact and ordinal.</param>
/// <param name="Tier">The tier before session taint is considered. Hard taint can raise a Tier 1 tool to Tier 2.</param>
/// <param name="Class">The side effect the tool has.</param>
/// <param name="Roles">The session roles that may call the tool. Calls from other roles are denied.</param>
/// <param name="Rate">Optional quota applied when a call is allowed. Breach denies the call.</param>
public sealed record ToolCapability(
    string Name,
    ToolTier Tier,
    CapabilityClass Class,
    SessionRoles Roles,
    RateRule? Rate = null);
