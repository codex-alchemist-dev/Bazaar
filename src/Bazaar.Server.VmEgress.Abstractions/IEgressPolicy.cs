using System.Net;

namespace Bazaar.Server.VmEgress;

/// <summary>How an <see cref="IEgressPolicy"/> call ended.</summary>
public enum EgressApplyOutcome
{
    /// <summary>The rules are in force: the proxy and the switch rules both carry them.</summary>
    Applied,

    /// <summary>The rules would widen the allowlist and the owner has not approved yet. Nothing changed.</summary>
    ApprovalPending,

    /// <summary>The owner refused the widening. Nothing changed.</summary>
    ApprovalDenied,

    /// <summary>The agent's egress is blocked by the kill switch. Nothing changed until <see cref="IEgressPolicy.ReleaseBlockAsync"/>.</summary>
    Blocked,

    /// <summary>An allowlist entry or the proxy endpoint is not valid. Nothing changed.</summary>
    Invalid,
}

/// <summary>Result of <see cref="IEgressPolicy.ApplyAsync"/>. <see cref="Detail"/> is for logs and cards and carries no secrets.</summary>
public sealed record EgressApplyResult(EgressApplyOutcome Outcome, string? Detail = null)
{
    /// <summary>True when the rules are in force.</summary>
    public bool IsApplied => Outcome == EgressApplyOutcome.Applied;
}

/// <summary>
/// Sets and drops an agent's egress (doc 15 <c>IEgressPolicy</c>). Growth of the allowlist needs owner approval, a shrink applies
/// at once, and <see cref="BlockAllAsync"/> is the kill-switch call.
/// </summary>
public interface IEgressPolicy
{
    /// <summary>
    /// Makes <paramref name="rules"/> the agent's egress. A rule set that allows anything the current approved set does not goes
    /// through the approval gate first. Entries that are only narrower or removed apply at once.
    /// </summary>
    Task<EgressApplyResult> ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default);

    /// <summary>
    /// Seeds an agent that has no stored allowlist with the install's default (<c>security.egress.default_allowlist</c>, owner
    /// approved at setup, so no approval request). An agent that already has rules keeps them.
    /// </summary>
    Task<EgressApplyResult> InitialiseAsync(string agentId, IPAddress proxyAddress, int proxyPort, CancellationToken ct = default);

    /// <summary>
    /// Kill switch: closes the proxy listener and every open tunnel, and replaces the switch allow rule with deny-all. Idempotent.
    /// Both steps are always tried; a failure in one is reported after the other has run. The stored allowlist is kept.
    /// </summary>
    Task BlockAllAsync(string agentId, CancellationToken ct = default);

    /// <summary>Lifts a block after the kill switch is released. The stored allowlist is re-applied; it never widens.</summary>
    Task<EgressApplyResult> ReleaseBlockAsync(string agentId, CancellationToken ct = default);
}
