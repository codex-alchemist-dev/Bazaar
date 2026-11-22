namespace Bazaar.Kodo.Standby;

/// <summary>What the failover decision says.</summary>
public enum FailoverAction
{
    /// <summary>Stay as standby.</summary>
    Stay,

    /// <summary>Ask the witnesses for the lease at <see cref="FailoverDecision.NewEpoch"/>.</summary>
    Promote,
}

/// <summary>Stable reason codes for <see cref="FailoverDecision"/>.</summary>
public static class FailoverReasons
{
    /// <summary>The node is not a standby.</summary>
    public const string NotStandby = "not_standby";

    /// <summary>No lease has ever been seen, so there is nothing to expire.</summary>
    public const string NoLeaseKnown = "no_lease_known";

    /// <summary>The newest known lease has not expired, with the skew margin.</summary>
    public const string LeaseNotExpired = "lease_not_expired";

    /// <summary>At least one witness still sees the primary, so it may still renew.</summary>
    public const string PrimaryCanRenew = "primary_can_renew";

    /// <summary>Fewer witnesses than the quorum agree the primary is unreachable.</summary>
    public const string NoQuorum = "no_quorum";

    /// <summary>Every condition holds.</summary>
    public const string Failover = "failover";
}

/// <summary>The inputs of <see cref="FailoverDecider.Decide"/>.</summary>
/// <param name="Role">This node's role.</param>
/// <param name="KnownLease">The newest lease from this node and every witness report (see <see cref="LeaseRecord.Newer"/>), or null.</param>
/// <param name="HighestEpochSeen">The highest epoch seen anywhere.</param>
/// <param name="Now">The caller's clock, in UTC.</param>
/// <param name="SkewMargin">Extra time past the expiry before the standby may act.</param>
/// <param name="Unreachable">The witness tally from <see cref="WitnessQuorumPolicy.EvaluateUnreachable"/>.</param>
public sealed record FailoverInput(
    StandbyRole Role,
    LeaseRecord? KnownLease,
    long HighestEpochSeen,
    DateTimeOffset Now,
    TimeSpan SkewMargin,
    QuorumResult Unreachable);

/// <summary>The outcome of the failover decision.</summary>
public sealed record FailoverDecision(FailoverAction Action, string Reason, FencingToken NewEpoch);

/// <summary>
/// The failover decision (doc 46 section 7.6). Pure and split-brain safe. It promotes only when every one of these holds: the node
/// is a standby, a lease is known and has expired (past the skew margin), no witness sees the primary, and a quorum of witnesses
/// agrees the primary is unreachable. Any other case stays. The new epoch is one above the highest ever seen, so it can never
/// equal or fall below an epoch a primary may still hold.
/// </summary>
public static class FailoverDecider
{
    /// <summary>Decides whether the standby may ask for the lease.</summary>
    public static FailoverDecision Decide(FailoverInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Role != StandbyRole.Standby)
        {
            return Stay(FailoverReasons.NotStandby);
        }

        if (input.KnownLease is null)
        {
            return Stay(FailoverReasons.NoLeaseKnown);
        }

        if (!input.KnownLease.IsExpired(input.Now, input.SkewMargin))
        {
            return Stay(FailoverReasons.LeaseNotExpired);
        }

        if (input.Unreachable.Reachable > 0)
        {
            return Stay(FailoverReasons.PrimaryCanRenew);
        }

        if (!input.Unreachable.Met)
        {
            return Stay(FailoverReasons.NoQuorum);
        }

        long highest = Math.Max(input.HighestEpochSeen, input.KnownLease.Epoch);
        return new FailoverDecision(FailoverAction.Promote, FailoverReasons.Failover, new FencingToken(highest).Next());
    }

    private static FailoverDecision Stay(string reason) => new(FailoverAction.Stay, reason, new FencingToken(0));
}
