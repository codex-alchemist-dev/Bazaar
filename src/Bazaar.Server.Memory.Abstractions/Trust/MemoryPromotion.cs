namespace Bazaar.Server.Memory.Trust;

/// <summary>The outcome of a promotion request. <see cref="Tier"/> is the tier after the request.</summary>
/// <param name="Allowed">True when the tier changed.</param>
/// <param name="Tier">The note's tier after the decision.</param>
/// <param name="Reason">Why the request was refused, or null when allowed.</param>
public sealed record MemoryPromotion(bool Allowed, MemoryTier Tier, string? Reason)
{
    /// <summary>A refused request, carrying the tier the note keeps.</summary>
    public static MemoryPromotion Refused(MemoryTier tier, string reason) => new(false, tier, reason);

    /// <summary>An allowed request, carrying the new tier.</summary>
    public static MemoryPromotion Accepted(MemoryTier tier) => new(true, tier, null);
}
