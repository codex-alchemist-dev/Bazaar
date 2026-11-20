using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals;

/// <summary>
/// Where a decision request came from. Only a tap on the card's own button is accepted. Dictated speech and any other
/// source never approve or deny anything (doc 29 Question 2, doc 15 flow step 4).
/// </summary>
public enum ApprovalInputSource
{
    /// <summary>The person tapped Approve or Deny on the card.</summary>
    CardButton,

    /// <summary>Transcribed speech. Always refused.</summary>
    Dictation,

    /// <summary>Text from an agent, a message or a notification action. Always refused.</summary>
    Other,
}

/// <summary>One decision the person asked for, with the exact card they saw.</summary>
/// <param name="Card">The card as displayed. The gate binds the biometric and the signature to its id and hash.</param>
/// <param name="Kind">The choice. Only <see cref="ApprovalDecisionKind.ApproveOnce"/> and <see cref="ApprovalDecisionKind.Deny"/> are supported.</param>
/// <param name="Source">Where the request came from.</param>
public sealed record ApprovalDecisionRequest(ApprovalCardDto Card, ApprovalDecisionKind Kind, ApprovalInputSource Source);

/// <summary>Why the gate refused. Every refusal means nothing was signed and nothing was sent, except <see cref="SendFailed"/>.</summary>
public enum GateRefusal
{
    /// <summary>The request did not come from the card's own button.</summary>
    NotFromCard,

    /// <summary>The decision is not one the phone can sign (edit-then-approve is desktop-only, approve-for-task is not yet in the signed message).</summary>
    UnsupportedDecision,

    /// <summary>The card is malformed (missing or badly shaped hash).</summary>
    InvalidCard,

    /// <summary>The card is not pending.</summary>
    CardNotPending,

    /// <summary>The card's expiry has passed.</summary>
    CardExpired,

    /// <summary>The card does not offer this decision.</summary>
    DecisionNotAvailable,

    /// <summary>The person cancelled the biometric prompt.</summary>
    BiometricCancelled,

    /// <summary>The biometric check failed.</summary>
    BiometricFailed,

    /// <summary>No biometric is usable on this device.</summary>
    BiometricUnavailable,

    /// <summary>A decision needing a biometric was submitted with no grant.</summary>
    BiometricRequired,

    /// <summary>The grant was not issued by this gate, was already used, or was withdrawn.</summary>
    GrantMissingOrReused,

    /// <summary>The grant is for another approval, another content hash or another decision.</summary>
    GrantMismatch,

    /// <summary>The grant is older than the grace period.</summary>
    GrantExpired,

    /// <summary>Another decision for this approval is in progress.</summary>
    AlreadyInProgress,

    /// <summary>A decision for this approval was already signed. A signed decision is never replayed.</summary>
    AlreadyDecided,

    /// <summary>The device key refused or failed to sign. Nothing was sent.</summary>
    SigningFailed,

    /// <summary>The signed decision could not be sent. It is not retried; the approval stays decided on this device.</summary>
    SendFailed,
}

/// <summary>
/// Proof of one fresh biometric success for one approval, one content hash and one decision. Only the gate creates it, and
/// the gate trusts its own record of the token, never the fields of this object.
/// </summary>
public sealed class BiometricGrant
{
    internal BiometricGrant(Guid token, Guid approvalId, string contentHash, ApprovalDecisionKind kind, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        Token = token;
        ApprovalId = approvalId;
        ContentHash = contentHash;
        Kind = kind;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>Random single-use token the gate looks up.</summary>
    public Guid Token { get; }

    /// <summary>Approval this grant is for.</summary>
    public Guid ApprovalId { get; }

    /// <summary>Content hash this grant is for.</summary>
    public string ContentHash { get; }

    /// <summary>Decision this grant is for.</summary>
    public ApprovalDecisionKind Kind { get; }

    /// <summary>When the biometric succeeded.</summary>
    public DateTimeOffset IssuedAt { get; }

    /// <summary>Last moment the grant can be used.</summary>
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>Result of asking for a biometric. <see cref="Grant"/> is set only when <see cref="Refusal"/> is null.</summary>
public sealed record GrantResult(GateRefusal? Refusal, BiometricGrant? Grant)
{
    /// <summary>True when a grant was issued.</summary>
    public bool IsGranted => Grant is not null;
}

/// <summary>Result of one decision. <see cref="Sent"/> is set only when the signed decision went out.</summary>
public sealed record GateResult(GateRefusal? Refusal, ApprovalDecisionDto? Sent)
{
    /// <summary>True when the decision was signed and sent.</summary>
    public bool IsSent => Sent is not null && Refusal is null;
}
