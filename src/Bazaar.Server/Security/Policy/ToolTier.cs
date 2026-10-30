namespace Bazaar.Server.Security.Policy;

/// <summary>
/// The approval ladder (doc 15, "Approval tiers"). Ordered from least to most restrictive, so a registry floor
/// can be checked with a comparison.
/// </summary>
public enum ToolTier
{
    /// <summary>Tier 0: autonomous. Public reads, own inbox, GC posts, repo reads.</summary>
    Autonomous = 0,

    /// <summary>Tier 1: autonomous with a GC notice. Replies in an approved thread, from an actor session.</summary>
    AutonomousWithNotice = 1,

    /// <summary>Tier 2: the owner approves first. Hard taint escalates Tier 1 to this tier.</summary>
    OwnerApproves = 2,

    /// <summary>Tier 3: a human performs the action. No agent may perform it.</summary>
    HumanPerforms = 3,

    /// <summary>Deny: never allowed, for any session or approval.</summary>
    Denied = 4,
}
