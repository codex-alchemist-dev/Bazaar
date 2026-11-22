namespace Bazaar.Server.VmEgress.Approvals;

/// <summary>Where one egress approval request stands, as the approvals service reports it.</summary>
public enum EgressTicketStatus
{
    /// <summary>Waiting for the owner.</summary>
    Pending,

    /// <summary>The owner approved. The grant has not been used yet.</summary>
    Approved,

    /// <summary>The owner refused.</summary>
    Denied,

    /// <summary>No decision arrived before expiry.</summary>
    Expired,
}

/// <summary>What the gate asks the approvals service to put in front of the owner.</summary>
/// <param name="AgentId">Agent whose allowlist would grow.</param>
/// <param name="Additions">The exact destinations, as the owner sees them on the card.</param>
/// <param name="AdditionsKey">Hash of the canonical additions set. The approval is bound to this and nothing else.</param>
/// <param name="Tier">Approval tier (doc 16 <c>vm.egress.allowlist_change_approval</c>, floor 2).</param>
public sealed record EgressApprovalRequest(string AgentId, IReadOnlyList<string> Additions, string AdditionsKey, int Tier);

/// <summary>One request on the approvals service. <see cref="AdditionsKey"/> must echo the request, or the gate refuses.</summary>
public sealed record EgressApprovalTicket(Guid Id, string AdditionsKey, EgressTicketStatus Status);

/// <summary>
/// Port to the owner approvals service (doc 15). The host adapts <c>ApprovalService</c> to it; this module never references
/// <c>Bazaar.Server</c>. Any exception, timeout or missing implementation is a denial.
/// </summary>
public interface IEgressApprovalPort
{
    /// <summary>
    /// Returns the open ticket for this agent and additions key, or files a new one (card to the owner) when none is open.
    /// A ticket that was denied or expired is reported once with that status and then retired, so the next call files afresh.
    /// </summary>
    Task<EgressApprovalTicket> RequestOrGetAsync(EgressApprovalRequest request, CancellationToken ct);

    /// <summary>Marks an approved ticket as used. True only for the one caller that wins; the grant cannot be used twice.</summary>
    Task<bool> ConsumeAsync(Guid ticketId, CancellationToken ct);
}

/// <summary>Port to session taint (doc 15). Egress growth is a Tier 2 action, so a hard-tainted agent gets none.</summary>
public interface IEgressTaintReader
{
    /// <summary>True when the agent's session has seen external content (hard taint). Failures throw; the gate denies.</summary>
    ValueTask<bool> IsHardTaintedAsync(string agentId, CancellationToken ct);
}
