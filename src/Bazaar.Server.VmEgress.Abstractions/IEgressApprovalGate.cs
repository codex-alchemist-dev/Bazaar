namespace Bazaar.Server.VmEgress;

/// <summary>The gate's answer to a request to widen an allowlist.</summary>
public enum EgressApprovalState
{
    /// <summary>The owner has approved exactly these additions. The grant is used up by this answer.</summary>
    Approved,

    /// <summary>A request is waiting for the owner.</summary>
    Pending,

    /// <summary>The owner refused, or no approval path is configured.</summary>
    Denied,
}

/// <summary>
/// Seam to the approval system (doc 15 approvals; doc 16 setting <c>vm.egress.allowlist_change_approval</c>, floor Tier 2).
/// The adapter over the approval service lives with the host. Contract: the first call for a given set of additions files a request
/// and returns <see cref="EgressApprovalState.Pending"/>; once the owner has approved, the next call for the same additions returns
/// <see cref="EgressApprovalState.Approved"/> once. It must never return Approved for additions the owner did not see.
/// </summary>
public interface IEgressApprovalGate
{
    /// <summary>Asks for approval to add <paramref name="additions"/> to the agent's allowlist, at tier <paramref name="tier"/> or stricter.</summary>
    Task<EgressApprovalState> RequestGrowthAsync(string agentId, IReadOnlyList<string> additions, int tier, CancellationToken ct = default);
}
