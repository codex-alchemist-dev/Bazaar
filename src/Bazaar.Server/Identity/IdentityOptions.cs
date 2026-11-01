using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Identity;

/// <summary>
/// Identity settings (doc 14, "Settings"). Defaults are the most protective documented values. Floors (for example
/// hard taint blocking Tier 2) are not options.
/// </summary>
public sealed record IdentityOptions
{
    /// <summary><c>identity.lease_ttl_minutes</c>.</summary>
    public int LeaseTtlMinutes { get; init; } = 15;

    /// <summary><c>identity.approval_expiry_hours</c>.</summary>
    public int ApprovalExpiryHours { get; init; } = 24;

    /// <summary><c>identity.totp.tier_ordinary</c>. Floor: Tier 2.</summary>
    public ToolTier TotpTierOrdinary { get; init; } = ToolTier.OwnerApproves;

    /// <summary><c>identity.totp.tier_sensitive</c>. Floor: Tier 2.</summary>
    public ToolTier TotpTierSensitive { get; init; } = ToolTier.HumanPerforms;

    /// <summary>Lower bound that <see cref="TotpTierOrdinary"/> and <see cref="TotpTierSensitive"/> may not go below.</summary>
    public static ToolTier TotpFloor => ToolTier.OwnerApproves;
}
