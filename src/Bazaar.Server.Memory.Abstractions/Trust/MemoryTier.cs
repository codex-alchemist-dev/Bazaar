namespace Bazaar.Server.Memory.Trust;

/// <summary>
/// Memory promotion tier (docs/design/08-memory-system.md, section 5). Ordered: a note only moves up one
/// step at a time, and only a human promotes it. <see cref="Quarantined"/> is terminal for retrieval.
/// </summary>
public enum MemoryTier
{
    /// <summary>Written by a quarantined agent. Never retrieved, never shared.</summary>
    Quarantined = 0,

    /// <summary>Written in a tainted session, or from external content. Retrieved only as labelled quoted data.</summary>
    UntrustedDerived = 1,

    /// <summary>Clean agent or system write. Retrieved normally.</summary>
    AgentVerified = 2,

    /// <summary>Human-approved. Retrieved normally and ranked higher.</summary>
    OwnerPromoted = 3,
}
