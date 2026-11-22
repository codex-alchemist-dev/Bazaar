using Bazaar.Server.AccountApproval.Abstractions;

namespace Bazaar.Server.AccountApproval;

/// <summary>
/// Operator settings for peer approval (doc 15 "Peer approval of account creation"). Bound from <c>Bazaar:AccountApproval</c>.
/// Shipped defaults are the protective ones: nobody is a listed peer until the operator lists them, and a person accepts terms.
/// </summary>
public sealed class AccountApprovalOptions
{
    public const string SectionName = "Bazaar:AccountApproval";

    /// <summary>The floor of <see cref="PeerQuorum"/> (owner card 2026-11-19: never below two).</summary>
    public const int MinimumQuorum = 2;

    /// <summary><c>security.account_approval.peer_approvers</c>: agent ids that may approve. Empty means every request goes to the owner.</summary>
    public List<string> PeerApprovers { get; set; } = [];

    /// <summary><c>security.account_approval.peer_quorum</c>: approvals needed. The operator may raise it, never below two.</summary>
    public int PeerQuorum { get; set; } = MinimumQuorum;

    /// <summary>
    /// <c>security.account_approval.agent_accepts_terms</c>: after approval the agent may accept the site's terms itself.
    /// A legal-exposure setting, so the shipped default is <c>false</c> (a person accepts).
    /// </summary>
    public bool AgentAcceptsTerms { get; set; }

    /// <summary><c>identity.approval_expiry_hours</c>: how long a peer approval stays valid. Default matches <c>security.approval.expiry_hours</c>.</summary>
    public int ApprovalExpiryHours { get; set; } = 24;

    /// <summary>
    /// Fewest voters a Council vote needs before a unanimous result may open a site without the owner. The docs do not give a
    /// number; one voter agreeing with itself is not a vote, so the default is two. Operators may raise it.
    /// </summary>
    public int MinUnanimousVoters { get; set; } = 2;

    /// <summary>Returns every problem with these values. Empty means valid. A quorum below two is rejected, not clamped.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PeerQuorum < MinimumQuorum)
        {
            errors.Add($"{AccountApprovalSettingKeys.PeerQuorum} must be at least {MinimumQuorum}.");
        }

        if (ApprovalExpiryHours < 1)
        {
            errors.Add($"{AccountApprovalSettingKeys.ApprovalExpiryHours} must be at least 1.");
        }

        if (MinUnanimousVoters < 1)
        {
            errors.Add("MinUnanimousVoters must be at least 1.");
        }

        if (PeerApprovers is null || PeerApprovers.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{AccountApprovalSettingKeys.PeerApprovers} must not contain blank ids.");
        }

        return errors;
    }
}
