namespace Bazaar.Server.AccountApproval.Abstractions;

/// <summary>Steps of a sign-up that only a person performs (doc 15 Tier 3). Peers never approve these away.</summary>
[Flags]
public enum PersonStep
{
    None = 0,
    Captcha = 1,
    Phone = 2,
    IdCheck = 4,
    Passkey = 8,
    RecoveryCode = 16,

    /// <summary>Accepting the site's legal terms. Added by the policy unless <c>security.account_approval.agent_accepts_terms</c> is on.</summary>
    LegalTerms = 32,
}

/// <summary>State of a site's <c>ServiceTerms</c> record (doc 36, doc 15 Deny row).</summary>
public enum ServiceTermsStatus
{
    /// <summary>No verified record. Deny for every action.</summary>
    Unverified,

    /// <summary>A verified record that allows the use asked for.</summary>
    Verified,

    /// <summary>A verified record whose terms forbid automation. Deny.</summary>
    ForbidsAutomation,
}

/// <summary>What the request asks to do. Only <see cref="CreateAccount"/> may be approved by peers.</summary>
public enum RequestedAction
{
    CreateAccount,

    /// <summary>Any other Tier 2 action. Peers cannot approve it.</summary>
    OtherTier2,
}

public enum PeerVoteKind
{
    Approve,
    Refuse,
}

/// <summary>Every rule that can decide an outcome. Recorded in the audit row and shown to the owner.</summary>
public enum DecisionRule
{
    // Denied.
    UnverifiedTerms,
    TermsForbidAutomation,
    AnotherPersonsName,

    // Needs the owner.
    OtherTier2Action,
    Spend,
    NotOnSignupWhitelist,
    ApprovalExpired,
    PeerRefused,
    TooFewEligiblePeers,
    InvalidQuorum,
    AccountScopeIsTier2,
    SplitVote,
    PeersRejectedSite,
    VoteTooSmall,
    VoteNotFinished,
    VoteDoesNotMatchSite,

    // Pending.
    AwaitingPeerApprovals,

    // Approved.
    PeerQuorumReached,
    UnanimousReadOnlyVote,
}

/// <summary>What the directory knows about one agent right now (doc 15 "Who may approve a given request").</summary>
/// <param name="SessionUntainted">The approving session has not read the site's own content or other hard-tainted input.</param>
/// <param name="RunsFrontierModel">The agent's turn runs on a model in its frontier whitelist (doc 05), not a fallback.</param>
/// <param name="Awake">The agent can take the request now.</param>
public sealed record ApproverStanding(bool SessionUntainted, bool RunsFrontierModel, bool Awake)
{
    /// <summary>The fail-closed standing: nobody is eligible.</summary>
    public static ApproverStanding None { get; } = new(false, false, false);
}

/// <summary>One listed agent and its current standing.</summary>
public sealed record ApproverCandidate(string AgentId, ApproverStanding Standing);

/// <summary>A finished Council vote on a motion (doc 10, doc 59 section 7). The Council writes no allowlist; this is its result.</summary>
/// <param name="MotionKind">Must be <see cref="SiteAllowlistMotion"/>.</param>
/// <param name="Subject">The site's registrable domain.</param>
/// <param name="VoteReference">The Council's id for the vote record, attached to every owner hand-off.</param>
/// <param name="Finished">The Council closed the vote (reveal and any follow-up round done).</param>
/// <param name="Eligible">Voters who could vote. The proposer is not among them (doc 10).</param>
public sealed record SiteVoteResult(
    string MotionKind,
    string Subject,
    string VoteReference,
    bool Finished,
    int Eligible,
    int Approve,
    int Reject,
    int Abstain)
{
    public const string SiteAllowlistMotion = "site_allowlist";
}

/// <summary>What a site request asks for.</summary>
public enum SiteScope
{
    /// <summary>Login-free reading in anonymous profiles.</summary>
    ReadOnly,

    /// <summary>A new account or logged-in use.</summary>
    Account,
}

/// <summary>A site's triage record, as carried by the <c>site_allowlist</c> motion (doc 59 section 7).</summary>
public sealed record SiteTriage(string Domain, ServiceTermsStatus Terms, SiteScope Scope, string? TermsUrl);

/// <summary>An agent's request to create an account (doc 15 "Peer approval of account creation").</summary>
/// <param name="RequestId">Unique per request. Approvals bind to it.</param>
/// <param name="Service">The exact service (registrable domain). Approvals bind to it.</param>
/// <param name="TermsUrl">The terms link the approving peers see.</param>
/// <param name="TermsVersionOrHash">Version or hash of the terms, stored with the approval.</param>
/// <param name="ServiceOnSignupWhitelist">The service is on <c>identity.signup_whitelist</c> (doc 14).</param>
/// <param name="SiteVote">The vote that opened the site, attached to any owner hand-off.</param>
public sealed record AccountApprovalRequest(
    string RequestId,
    string RequesterAgentId,
    string Service,
    RequestedAction Action,
    ServiceTermsStatus Terms,
    string? TermsUrl,
    string? TermsVersionOrHash,
    bool ServiceOnSignupWhitelist,
    bool InvolvesSpend,
    bool InAnotherPersonsName,
    PersonStep PersonSteps,
    DateTimeOffset CreatedAt,
    SiteVoteResult? SiteVote = null);

/// <summary>One senior peer's answer to a request.</summary>
/// <param name="Service">Must equal the request's service (approvals bind to the exact service and agent).</param>
/// <param name="ModelId">The model that ran the approving turn (audited).</param>
/// <param name="ReasoningHash">Hash of the approving turn's reasoning (audited). The reasoning itself is not carried here.</param>
public sealed record PeerVote(
    string RequestId,
    string Service,
    string ApproverAgentId,
    PeerVoteKind Kind,
    string ModelId,
    string ReasoningHash,
    DateTimeOffset CastAt);

/// <summary>What the owner (or the reader of an audit row) is shown beside a hand-off.</summary>
public sealed record ApprovalEvidence(
    string Service,
    string RequesterAgentId,
    string? TermsUrl,
    SiteVoteResult? SiteVote,
    IReadOnlyList<PeerVote> PeerVotes);

/// <summary>Result of the account-creation approval policy.</summary>
public abstract record AccountApprovalDecision(string RequestId, DecisionRule Rule);

/// <summary>The configured number of senior peers approved. Only the listed person steps remain.</summary>
/// <param name="PersonSteps">Steps a person still performs: CAPTCHA, phone, ID and the like, plus legal terms unless the agent may accept them.</param>
/// <param name="AgentMayAcceptTerms">The agent itself may accept <paramref name="TermsToAccept"/> as part of this sign-up.</param>
/// <param name="ExpiresAt">The approval lapses then (<c>identity.approval_expiry_hours</c>).</param>
public sealed record ApprovedByPeers(
    string RequestId,
    DecisionRule Rule,
    IReadOnlyList<PeerVote> Approvals,
    PersonStep PersonSteps,
    bool AgentMayAcceptTerms,
    string? TermsToAccept,
    string? TermsVersionOrHash,
    DateTimeOffset ExpiresAt) : AccountApprovalDecision(RequestId, Rule);

/// <summary>The request goes to the owner as a Tier 2 approval, with the evidence attached.</summary>
public sealed record NeedsOwner(string RequestId, DecisionRule Rule, ApprovalEvidence Evidence)
    : AccountApprovalDecision(RequestId, Rule);

/// <summary>Deny: no approval, by anyone, can change this.</summary>
public sealed record Denied(string RequestId, DecisionRule Rule) : AccountApprovalDecision(RequestId, Rule);

/// <summary>Waiting for more peer approvals.</summary>
public sealed record Pending(string RequestId, int Remaining, int Quorum)
    : AccountApprovalDecision(RequestId, DecisionRule.AwaitingPeerApprovals);

/// <summary>Result of the site vote policy (doc 59 section 7, DECISIONS-LOG round 31).</summary>
public enum SiteAccessOutcome
{
    /// <summary>Unanimous: the site opens for login-free reading in anonymous profiles, with no owner step.</summary>
    OpenReadOnly,

    /// <summary>Owner Tier 2 approval, vote attached.</summary>
    NeedsOwner,

    /// <summary>Deny whatever the vote says.</summary>
    Denied,

    /// <summary>The vote is not finished yet.</summary>
    Pending,
}

public sealed record SiteAccessDecision(
    string Domain,
    SiteAccessOutcome Outcome,
    DecisionRule? Rule,
    string? VoteReference,
    ApprovalEvidence? Evidence);
