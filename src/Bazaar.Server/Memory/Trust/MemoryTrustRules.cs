using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Trust;

/// <summary>
/// Pure memory trust rules: the starting tier of a write, which actors may promote, how each tier is
/// retrieved, and which tiers may be copied to Hive Mind. No I/O, so the whole table is testable.
/// Fixed floors (doc 08, "Settings never loosen"): only humans promote, only the owner reaches
/// <see cref="MemoryTier.OwnerPromoted"/>, promotion moves one step at a time, and no rule here clears taint.
/// </summary>
public static class MemoryTrustRules
{
    /// <summary>The tier a new note starts at, from what the server knows about the write.</summary>
    public static MemoryTier TierForWrite(MemoryWriteInput write)
    {
        ArgumentNullException.ThrowIfNull(write);

        // Quarantined sources are checked first: nothing they write is ever usable.
        if (write.SourceTrust == ContentTrust.Quarantined)
        {
            return MemoryTier.Quarantined;
        }

        // External lineage, external sources and any hard-tainted session start at T1 (doc 08, boundary mapping).
        if (write.SourceTrust == ContentTrust.External
            || write.InheritsExternal
            || write.SessionState == TaintState.Hard)
        {
            return MemoryTier.UntrustedDerived;
        }

        // Owner writes start at T3 (the owner is the final authority). Member-human writes start at T2:
        // doc 08 lets a member-human set T3, but section 6 has the owner promote, so T3 stays owner-only.
        if (write.SourceTrust == ContentTrust.Owner && write.Actor == MemoryActor.Owner)
        {
            return MemoryTier.OwnerPromoted;
        }

        return MemoryTier.AgentVerified;
    }

    /// <summary>
    /// Whether an actor may move a note from one tier to another. Only single steps up are allowed:
    /// T1 to T2 by a human (owner or member-human), T2 to T3 by the owner only. Everything else is refused,
    /// including any move out of <see cref="MemoryTier.Quarantined"/>, any demotion, and any skip.
    /// </summary>
    public static MemoryPromotion Promote(MemoryTier current, MemoryTier target, MemoryActor actor)
    {
        if (target == MemoryTier.Quarantined || target < current)
        {
            return MemoryPromotion.Refused(current, "Only upward promotion is allowed.");
        }

        if (current == MemoryTier.Quarantined)
        {
            return MemoryPromotion.Refused(current, "Quarantined notes cannot be promoted.");
        }

        if (target == current)
        {
            return MemoryPromotion.Refused(current, "The note is already at that tier.");
        }

        if (target != current + 1)
        {
            return MemoryPromotion.Refused(current, "Promotion moves one tier at a time.");
        }

        if (!IsHuman(actor))
        {
            return MemoryPromotion.Refused(current, "Only a human can promote a note.");
        }

        if (target == MemoryTier.OwnerPromoted && actor != MemoryActor.Owner)
        {
            return MemoryPromotion.Refused(current, "Only the owner can promote a note to owner-promoted.");
        }

        return MemoryPromotion.Accepted(target);
    }

    /// <summary>
    /// How a note of this tier may be shown to an agent. Quarantined notes are excluded; untrusted-derived
    /// notes are labelled quoted data only.
    /// </summary>
    public static MemoryRetrieval RetrievalFor(MemoryTier tier) => tier switch
    {
        MemoryTier.Quarantined => MemoryRetrieval.Excluded,
        MemoryTier.UntrustedDerived => MemoryRetrieval.LabelledQuotedData,
        MemoryTier.AgentVerified => MemoryRetrieval.Normal,
        MemoryTier.OwnerPromoted => MemoryRetrieval.Preferred,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown memory tier."),
    };

    /// <summary>Hive Mind copies may come only from T2 or T3 notes (doc 08, section 6).</summary>
    public static bool IsHiveEligible(MemoryTier tier) =>
        tier is MemoryTier.AgentVerified or MemoryTier.OwnerPromoted;

    /// <summary>Whether the actor is a person. Votes, peer reviews, agents and system work are not.</summary>
    public static bool IsHuman(MemoryActor actor) => actor is MemoryActor.Owner or MemoryActor.MemberHuman;
}
