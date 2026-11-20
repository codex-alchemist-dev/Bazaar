namespace Bazaar.Server.AccountApproval.Abstractions;

/// <summary>
/// Who is senior, frontier and untainted right now. The host adapts the roster (doc 07), the model whitelists (doc 05) and the
/// session taint state (doc 15). A throw or a null answer means "not eligible".
/// </summary>
public interface IApproverDirectory
{
    Task<ApproverStanding?> GetStandingAsync(string agentId, CancellationToken ct = default);
}

/// <summary>What the owner is asked to approve as a Tier 2 action.</summary>
public sealed record OwnerApprovalRequest(string RequestId, string Reason, ApprovalEvidence Evidence);

/// <summary>
/// Hands a request to the owner's approval queue (doc 15 Tier 2; host adapter over the approvals module). Called once per request.
/// </summary>
public interface IOwnerApprovalHandOff
{
    Task RequestOwnerApprovalAsync(OwnerApprovalRequest request, CancellationToken ct = default);
}

/// <summary>Setting keys (doc 15 settings table).</summary>
public static class AccountApprovalSettingKeys
{
    public const string PeerApprovers = "security.account_approval.peer_approvers";
    public const string PeerQuorum = "security.account_approval.peer_quorum";
    public const string AgentAcceptsTerms = "security.account_approval.agent_accepts_terms";
    public const string ApprovalExpiryHours = "identity.approval_expiry_hours";
}
