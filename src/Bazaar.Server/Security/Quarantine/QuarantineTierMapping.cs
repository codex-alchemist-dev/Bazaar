using Bazaar.Server.Memory.Trust;

namespace Bazaar.Server.Security.Quarantine;

/// <summary>
/// Maps a subject's quarantine state to its memory and Vault tier (doc 08, section 5; doc 15, "Writes").
/// Pure: the caller supplies the tier the write would have had without quarantine.
/// </summary>
public static class QuarantineTierMapping
{
    /// <summary>
    /// The tier of a note whose subject has this quarantine status. Quarantined is T0. A released subject is capped at
    /// T1, since release restores visibility but not its taint lineage; only a human can promote it from there. A
    /// subject with no record keeps the tier its write earned.
    /// </summary>
    /// <param name="quarantine">Status of the subject, or null when it has no record.</param>
    /// <param name="unquarantinedTier">The tier the write earned from <see cref="MemoryTrustRules.TierForWrite"/>.</param>
    public static MemoryTier MemoryTierFor(QuarantineStatus? quarantine, MemoryTier unquarantinedTier) => quarantine switch
    {
        null => unquarantinedTier,
        QuarantineStatus.Quarantined => MemoryTier.Quarantined,
        QuarantineStatus.Released => (MemoryTier)Math.Min((int)unquarantinedTier, (int)MemoryTier.UntrustedDerived),
        _ => throw new ArgumentOutOfRangeException(nameof(quarantine), quarantine, "Unknown quarantine status."),
    };
}
