namespace Bazaar.Kodo.Standby;

/// <summary>Whether a node must fence itself, and why.</summary>
public sealed record SelfFenceDecision(bool Fence, string? Cause);

/// <summary>
/// The demotion rule (doc 46 section 7.6). A primary fences itself the moment its own lease expires, with no margin, before the
/// standby's margin has passed. It also fences when its lease is missing or for another epoch, or when a newer epoch exists.
/// Other roles are left alone. Pure.
/// </summary>
public static class SelfFence
{
    /// <summary>Evaluates a node's own state.</summary>
    public static SelfFenceDecision Evaluate(StandbyRole role, long ownEpoch, LeaseRecord? ownLease, long highestEpochSeen, DateTimeOffset now)
    {
        if (role != StandbyRole.Primary)
        {
            return new SelfFenceDecision(false, null);
        }

        if (highestEpochSeen > ownEpoch)
        {
            return new SelfFenceDecision(true, StandbyCauses.Superseded);
        }

        if (ownLease is null || ownLease.Epoch != ownEpoch)
        {
            return new SelfFenceDecision(true, StandbyCauses.LeaseMissing);
        }

        return ownLease.IsExpired(now)
            ? new SelfFenceDecision(true, StandbyCauses.LeaseLost)
            : new SelfFenceDecision(false, null);
    }
}
